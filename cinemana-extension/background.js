// Background script for Cinemana Download Button

// Chrome ignores the `filename` option in chrome.downloads.download() when
// the url is a data: URI, silently falling back to "download.<ext>". To
// force the correct name, we track the desired filename per download URL
// and re-assert it via onDeterminingFilename, which Chrome always respects.
const pendingFilenames = new Map();

chrome.downloads.onDeterminingFilename.addListener((downloadItem, suggest) => {
  const desired = pendingFilenames.get(downloadItem.url);
  if (desired) {
    pendingFilenames.delete(downloadItem.url);
    suggest({ filename: desired, conflictAction: 'uniquify' });
  } else {
    suggest();
  }
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.action === 'download') {
    startChromeDownload(message.url, message.filename);
    sendResponse({ ok: true });
    return false;
  }

  if (message.action === 'downloadSubtitle') {
    handleSubtitleDownload(message, sender)
      .then(() => sendResponse({ ok: true }))
      .catch(error => {
        console.error('[Cinemana DL Extension] Subtitle download failed:', error);
        sendResponse({
          ok: false,
          error: error.message || 'Could not download subtitle.'
        });
      });
    return true;
  }

  if (message.action === 'batchDownloadEpisode') {
    handleBatchEpisode(message)
      .then(result => sendResponse({ ok: true, ...result }))
      .catch(error => {
        console.error('[Cinemana DL Extension] Episode download failed:', error);
        sendResponse({ ok: false, error: error.message || 'Download failed.' });
      });
    return true;
  }

  return false;
});

// Batch (season) downloads go straight into a folder without a Save As
// prompt per file. The subtitle is saved as .srt with the same base name as
// the video so players (VLC, MPC, TVs) pick it up automatically.
async function handleBatchEpisode(message) {
  const videoPath = sanitizeDownloadPath(message.video.filename);
  queueDownload(message.video.url, videoPath);

  let subtitleError = null;
  if (message.subtitle) {
    try {
      const fetched = await fetchSubtitleText(normalizeSubtitleUrl(message.subtitle.url));
      const srt = convertToSrt(fetched.text, detectSubtitleTextExtension(fetched.text, fetched.url));
      // A BOM helps Windows players detect UTF-8 Arabic text correctly.
      const dataUrl = makeTextDataUrl('\uFEFF' + srt, 'application/x-subrip;charset=utf-8');
      queueDownload(dataUrl, sanitizeDownloadPath(message.subtitle.filename));
    } catch (error) {
      subtitleError = error.message || String(error);
    }
  }

  return { subtitleError };
}

function queueDownload(url, filename) {
  pendingFilenames.set(url, filename);
  chrome.downloads.download({ url, filename, saveAs: false }, () => {
    if (chrome.runtime.lastError) {
      console.error('Download failed:', chrome.runtime.lastError);
      pendingFilenames.delete(url);
    }
  });
}

function sanitizeDownloadPath(path) {
  return String(path || '')
    .split('/')
    .map(part => part
      .replace(/[<>:"\\|?*\x00-\x1F]/g, '_')
      .replace(/\s+/g, '_')
      .replace(/_+/g, '_')
      .replace(/^[_.]+|[_.]+$/g, ''))
    .filter(Boolean)
    .join('/') || 'Cinemana_Video';
}

function convertToSrt(text, sourceExt) {
  const source = String(text || '').replace(/\uFEFF/g, '').replace(/\r\n/g, '\n').trim();
  if (sourceExt === 'srt' && !/^WEBVTT\b/i.test(source)) {
    return source + '\n';
  }

  const cues = source
    .replace(/^WEBVTT[^\n]*\n*/i, '')
    .split(/\n{2,}/)
    .map(block => block.split('\n'))
    .filter(lines => lines.some(line => line.includes('-->')))
    .map(lines => {
      const timeIndex = lines.findIndex(line => line.includes('-->'));
      const [start, end] = lines[timeIndex].split('-->').map(t => toSrtTime(t.trim().split(/\s+/)[0]));
      const body = lines.slice(timeIndex + 1).join('\n').replace(/<[^>]+>/g, '');
      return `${start} --> ${end}\n${body}`;
    });

  return cues.map((cue, index) => `${index + 1}\n${cue}`).join('\n\n') + '\n';
}

function toSrtTime(value) {
  const parts = String(value).replace(',', '.').split(':');
  if (parts.length === 2) parts.unshift('00');
  const [h, m, rest] = parts;
  const [sec, ms = '0'] = rest.split('.');
  return `${h.padStart(2, '0')}:${m.padStart(2, '0')}:${sec.padStart(2, '0')},${ms.padEnd(3, '0').slice(0, 3)}`;
}

function startChromeDownload(url, filename, fallbackBase = 'Cinemana_Subtitle') {
  const safeFilename = sanitizeDownloadFilename(filename, fallbackBase);
  console.log('[Cinemana DL Extension] Starting download:', safeFilename);

  // Data: URLs don't reliably honor `filename` below, so also register it
  // here to be re-applied by the onDeterminingFilename listener above.
  pendingFilenames.set(url, safeFilename);

  chrome.downloads.download({
    url: url,
    filename: safeFilename,
    saveAs: true
  }, downloadId => {
    if (chrome.runtime.lastError) {
      console.error('Download failed:', chrome.runtime.lastError);
      pendingFilenames.delete(url);
    } else {
      console.log('Download started with ID:', downloadId);
    }
  });
}

async function handleSubtitleDownload(message, sender) {
  const sourceUrl = normalizeSubtitleUrl(message.url);
  const fetched = await fetchSubtitleText(sourceUrl);
  const fallbackBase = getSubtitleFallbackBase(message, sender);

  const sourceExt = detectSubtitleTextExtension(fetched.text, fetched.url);
  const normalizedText = normalizeSubtitleTextForDownload(fetched.text, sourceExt);

  const dataUrl = makeTextDataUrl(normalizedText, 'text/vtt;charset=utf-8');

  const filename = ensureFileExtension(message.filename || `${fallbackBase}.vtt`, 'vtt');

  startChromeDownload(dataUrl, filename, fallbackBase);
}

function sanitizeDownloadFilename(filename, fallbackBase = 'Cinemana_Subtitle') {
  const safe = String(filename || '')
    .replace(/[<>:"/\\|?*\x00-\x1F]/g, '_')
    .replace(/\s+/g, '_')
    .replace(/_+/g, '_')
    .replace(/^_+|_+$/g, '');

  if (!safe || /^download(?:\.\w+)?$/i.test(safe)) {
    const fallback = sanitizeFilenameBase(fallbackBase);
    return `${fallback && !/^download$/i.test(fallback) ? fallback : 'Cinemana_Subtitle'}.vtt`;
  }

  return safe;
}

function ensureFileExtension(filename, ext) {
  const safeExt = ext.replace(/^\./, '');
  const base = String(filename || '').replace(/\.[a-z0-9]+$/i, '');
  return `${base || 'Cinemana_Subtitle'}.${safeExt}`;
}

function getSubtitleFallbackBase(message, sender) {
  return sanitizeFilenameBase(
    message.pageTitle ||
    (sender && sender.tab && sender.tab.title) ||
    'Cinemana_Subtitle'
  ).replace(/_vtt$/i, '');
}

function sanitizeFilenameBase(value) {
  return String(value || '')
    .replace(/\.[a-z0-9]+$/i, '')
    .replace(/\s*[-|]\s*Cinemana.*$/i, '')
    .replace(/[<>:"/\\|?*\x00-\x1F]/g, '_')
    .replace(/\s+/g, '_')
    .replace(/_+/g, '_')
    .replace(/^_+|_+$/g, '');
}

function normalizeSubtitleUrl(url) {
  const normalized = String(url || '').replace(/&amp;/g, '&').trim();
  if (!/^https?:\/\//i.test(normalized)) {
    throw new Error('Subtitle URL is missing or invalid.');
  }
  return normalized;
}

async function fetchSubtitleText(url) {
  const response = await fetch(url, {
    cache: 'no-store',
    credentials: 'omit',
    redirect: 'follow'
  });
  const text = await response.text();
  const finalUrl = response.url || url;

  if (!response.ok) {
    throw new Error(`Subtitle request failed: HTTP ${response.status}.`);
  }

  if (looksLikeHtml(text)) {
    const embeddedUrl = extractSubtitleUrlsFromText(text)[0];
    if (embeddedUrl && embeddedUrl !== url) {
      return fetchSubtitleText(embeddedUrl);
    }

    throw new Error('Cinemana returned an HTML page instead of the subtitle file.');
  }

  if (!looksLikeSubtitleText(text)) {
    throw new Error('The downloaded file does not look like a VTT or SRT subtitle.');
  }

  return { text, url: finalUrl };
}

function extractSubtitleUrlsFromText(text) {
  const source = String(text || '').replace(/&amp;/g, '&');
  return source.match(/https?:\/\/[^\s"'<>]+\.(?:vtt|srt)(?:\?[^\s"'<>]*)?/gi) || [];
}

function looksLikeHtml(text) {
  return /<!doctype\s+html|<html[\s>]|<body[\s>]/i.test(String(text || '').slice(0, 1000));
}

function looksLikeSubtitleText(text) {
  const source = String(text || '').trim();
  return /^WEBVTT\b/i.test(source) ||
    /\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}\s*-->\s*\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}/.test(source) ||
    /\d{1,2}:\d{2}[,.]\d{1,3}\s*-->\s*\d{1,2}:\d{2}[,.]\d{1,3}/.test(source);
}

function getSubtitleExtension(url) {
  const cleanUrl = String(url || '').split('?')[0].toLowerCase();
  if (cleanUrl.endsWith('.srt')) return 'srt';
  return 'vtt';
}

function detectSubtitleTextExtension(text, url) {
  if (/^WEBVTT\b/i.test(String(text || '').trim())) return 'vtt';
  return getSubtitleExtension(url);
}

function normalizeSubtitleTextForDownload(text, sourceExt) {
  if (sourceExt === 'srt') {
    return convertSrtToVtt(text);
  }

  if (!/^WEBVTT\b/i.test(String(text || '').trim())) {
    return `WEBVTT\n\n${String(text || '').trim()}\n`;
  }

  return String(text || '').replace(/\r\n/g, '\n').trim() + '\n';
}

function convertSrtToVtt(srtText) {
  const body = String(srtText || '')
    .replace(/\uFEFF/g, '')
    .replace(/\r\n/g, '\n')
    .replace(/(\d{1,2}:\d{2}:\d{2}),(\d{1,3})/g, '$1.$2')
    .trim();
  return `WEBVTT\n\n${body}\n`;
}

function makeTextDataUrl(text, mimeType) {
  const bytes = new TextEncoder().encode(text);
  let binary = '';
  const chunkSize = 0x8000;
  for (let i = 0; i < bytes.length; i += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunkSize));
  }
  return `data:${mimeType};base64,${btoa(binary)}`;
}