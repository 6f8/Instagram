// Cinemana Download Button Content Script (Isolated World)

let videoFiles = [];
let subtitleFiles = [];
let movieTitle = "Cinemana_Video";
let mediaMetadata = {};
let lastUrl = location.href;
let currentVideoKey = getVideoKeyFromPageUrl(location.href);
let mainWorldInjected = false;
let injectQueued = false;

console.log("[Cinemana DL Extension] Content script loaded. Current URL:", lastUrl);

function getExtensionRuntime() {
  try {
    if (typeof chrome !== 'undefined' && chrome.runtime && chrome.runtime.id) {
      return chrome.runtime;
    }
  } catch (error) {
    return null;
  }

  return null;
}

function alertExtensionContextInvalidated() {
  alert('The Cinemana Download Button extension was reloaded. Please refresh this Cinemana tab, then try downloading again.');
}

function sendDownloadMessage(payload, onResponse) {
  const runtime = getExtensionRuntime();
  if (!runtime) {
    alertExtensionContextInvalidated();
    return false;
  }

  try {
    runtime.sendMessage(payload, response => {
      try {
        if (chrome.runtime.lastError) {
          console.error('[Cinemana DL Extension] Download message failed:', chrome.runtime.lastError);
        } else if (typeof onResponse === 'function') {
          onResponse(response);
        }
      } catch (error) {}
    });
    return true;
  } catch (error) {
    console.error('[Cinemana DL Extension] Extension context is no longer available:', error);
    alertExtensionContextInvalidated();
    return false;
  }
}

function injectMainWorldInterceptor() {
  if (mainWorldInjected || document.documentElement.dataset.cinemanaDlInjected === 'true') {
    return;
  }

  const runtime = getExtensionRuntime();
  if (!runtime) {
    return;
  }

  mainWorldInjected = true;
  document.documentElement.dataset.cinemanaDlInjected = 'true';

  const script = document.createElement('script');
  script.src = runtime.getURL('main-world.js');
  script.onload = () => script.remove();
  (document.head || document.documentElement).appendChild(script);
}

injectMainWorldInterceptor();

function getVideoKeyFromPageUrl(url = location.href) {
  try {
    const parsedUrl = new URL(url, location.href);
    const episodeId = parsedUrl.searchParams.get('lastEpisodeVideoID');
    if (episodeId) {
      return episodeId;
    }

    const pathMatch = parsedUrl.pathname.match(/\/video\/(?:ar|en)\/(\d+)/i);
    return pathMatch ? pathMatch[1] : '';
  } catch (error) {
    const queryMatch = String(url || '').match(/[?&]lastEpisodeVideoID=(\d+)/i);
    if (queryMatch) {
      return queryMatch[1];
    }

    const pathMatch = String(url || '').match(/\/video\/(?:ar|en)\/(\d+)/i);
    return pathMatch ? pathMatch[1] : '';
  }
}

function isApiDataForCurrentEpisode(pageUrl) {
  if (!pageUrl) {
    return true;
  }

  const capturedVideoKey = getVideoKeyFromPageUrl(pageUrl);
  const activeVideoKey = getVideoKeyFromPageUrl(location.href) || currentVideoKey;
  return !capturedVideoKey || !activeVideoKey || capturedVideoKey === activeVideoKey;
}

function handleRouteChange(nextUrl = location.href) {
  if (nextUrl === lastUrl) {
    return;
  }

  console.log("[Cinemana DL Extension] Route changed from", lastUrl, "to", nextUrl);
  const previousVideoKey = currentVideoKey;
  const nextVideoKey = getVideoKeyFromPageUrl(nextUrl);
  const isSameVideo = previousVideoKey && nextVideoKey && previousVideoKey === nextVideoKey;

  lastUrl = nextUrl;
  currentVideoKey = nextVideoKey;

  if (!isSameVideo) {
    videoFiles = [];
    subtitleFiles = [];
    movieTitle = "Cinemana_Video";
    mediaMetadata = {};
  }

  const oldContainer = document.getElementById('cinemana-download-container');
  if (oldContainer) {
    oldContainer.remove();
  }

  [100, 500, 1200].forEach(delay => {
    setTimeout(() => {
      injectDownloadButton();
      updateDropdownUI();
    }, delay);
  });
}

// Listen to postMessage from the main world
window.addEventListener('message', function(event) {
  if (event.source !== window || !event.data) {
    return;
  }

  if (event.data.type === 'CINEMANA_LOCATION_CHANGED') {
    handleRouteChange(event.data.url || location.href);
    return;
  }

  if (event.data.type !== 'CINEMANA_API_DATA') {
    return;
  }

  handleRouteChange(location.href);

  const { url, data, pageUrl } = event.data;

  if (!isApiDataForCurrentEpisode(pageUrl)) {
    console.log("[Cinemana DL Extension] Ignoring stale media data captured on", pageUrl, "while current URL is", location.href);
    return;
  }

  const isVideoFilesResponse = url.includes('/videoFiles') || url.includes('/allVideoFiles') || (data && Array.isArray(data) && data.length > 0 && data[0].videoUrl);
  const isSubtitleResponse = isSubtitleUrl(url) || url.includes('/translation') || url.includes('/transfile') || url.includes('/subtitles') || (data && Array.isArray(data) && data.length > 0 && (data[0].fileUrl || data[0].srt || data[0].vtt));

  // Video-file and subtitle-listing responses are arrays of track objects like
  // { name: "Arabic", url: "..." }. Their "name"/"title" fields are track
  // labels, not the show name, so running the generic metadata scan on them
  // would overwrite the real title with a subtitle language name. Only scan
  // responses that aren't already identified as one of those two kinds.
  if (!isVideoFilesResponse && !isSubtitleResponse) {
    parseMediaMetadata(data);
  }

  // Intercept video file details
  if (isVideoFilesResponse) {
    console.log("[Cinemana DL Extension] Intercepted video files:", data);
    parseVideoFiles(data);
    updateDropdownUI();
  }

  // Intercept subtitle/translation details
  if (isSubtitleResponse) {
    console.log("[Cinemana DL Extension] Intercepted subtitles:", data);
    parseSubtitles(data || { url });
    updateDropdownUI();
  }
});

// Detect SPA URL changes and reset state
setInterval(() => {
  handleRouteChange(location.href);
}, 800);

// Recursively search for video URLs and resolutions
function parseVideoFiles(data) {
  const foundFiles = [];
  
  function search(obj) {
    if (!obj) return;
    if (typeof obj === 'string') {
      return;
    }
    if (Array.isArray(obj)) {
      obj.forEach(item => search(item));
      return;
    }
    if (typeof obj === 'object') {
      const url = obj.videoUrl || obj.fileUrl || obj.url || obj.link;
      const res = normalizeVideoQuality(obj.resolution || obj.quality || obj.label || obj.name || url);
      if (url && typeof url === 'string' && (url.includes('.mp4') || url.includes('.m3u8') || url.includes('.mkv') || url.includes('video'))) {
        foundFiles.push({
          url: url,
          quality: res || 'HD',
          type: obj.container || 'mp4'
        });
      } else {
        for (let key in obj) {
          if (obj.hasOwnProperty(key)) {
            search(obj[key]);
          }
        }
      }
    }
  }

  search(data);
  if (foundFiles.length > 0) {
    // Unique list by quality
    const seen = new Set();
    videoFiles = foundFiles.filter(file => {
      const duplicate = seen.has(file.quality);
      seen.add(file.quality);
      return !duplicate;
    }).sort((a, b) => getVideoQualityRank(b.quality) - getVideoQualityRank(a.quality));
    console.log("[Cinemana DL Extension] Parsed video files:", videoFiles);
  }
}

function normalizeVideoQuality(value) {
  const text = String(value || '').toLowerCase();
  if (/(4k|2160|uhd)/i.test(text)) return '4K';
  if (/(2k|1440|qhd)/i.test(text)) return '2K';
  const resolution = text.match(/(1080|720|480|360|240)\s*p?/i);
  if (resolution) return `${resolution[1]}p`;
  if (/full\s*hd|fhd/i.test(text)) return '1080p';
  if (/\bhd\b/i.test(text)) return '720p';
  if (/\bsd\b/i.test(text)) return '360p';
  return value || 'HD';
}

function getVideoQualityRank(quality) {
  const text = String(quality || '').toLowerCase();
  if (text.includes('4k') || text.includes('2160') || text.includes('uhd')) return 2160;
  if (text.includes('2k') || text.includes('1440') || text.includes('qhd')) return 1440;
  if (text.includes('1080') || text.includes('fhd')) return 1080;
  if (text.includes('720') || /\bhd\b/.test(text)) return 720;
  if (text.includes('480')) return 480;
  if (text.includes('360') || /\bsd\b/.test(text)) return 360;
  if (text.includes('240')) return 240;
  return parseInt(text, 10) || 0;
}

// Recursively search for subtitle URLs and languages
function parseSubtitles(data) {
  const foundSubs = [];
  
  function search(obj) {
    if (!obj) return;
    if (typeof obj === 'string') {
      extractSubtitleUrlsFromText(obj).forEach(url => {
        const inferred = inferSubtitleLanguage({ url });
        foundSubs.push({
          url: normalizeDownloadUrl(url),
          lang: inferred.label.charAt(0).toUpperCase() + inferred.label.slice(1),
          ext: getSubtitleExtension(url)
        });
      });
      return;
    }
    if (Array.isArray(obj)) {
      obj.forEach(item => search(item));
      return;
    }
    if (typeof obj === 'object') {
      const url = obj.fileUrl || obj.url || obj.link || obj.srt || obj.vtt;
      let lang = obj.lang || obj.language || obj.title || obj.name || obj.label;
      if (url && typeof url === 'string' && isSubtitleFileUrl(url)) {
        if (!lang) {
          if (/(^|[_/-])ar([_./-]|$)|arabic/i.test(url)) lang = 'Arabic';
          else if (/(^|[_/-])en([_./-]|$)|english/i.test(url)) lang = 'English';
          else lang = 'Sub';
        }
        const inferred = inferSubtitleLanguage({ url, lang });
        foundSubs.push({
          url: normalizeDownloadUrl(url),
          lang: inferred.label.charAt(0).toUpperCase() + inferred.label.slice(1),
          ext: getSubtitleExtension(url)
        });
      } else {
        for (let key in obj) {
          if (obj.hasOwnProperty(key)) {
            search(obj[key]);
          }
        }
      }
    }
  }

  search(data);
  addSubtitleFiles(foundSubs);
}

function isSubtitleUrl(url) {
  return typeof url === 'string' && /(\.vtt|\.srt|subtitle|subtitles|translation|transfile)/i.test(url);
}

function isSubtitleFileUrl(url) {
  return /\.(vtt|srt)(?:\?|$)/i.test(String(url || ''));
}

function extractSubtitleUrlsFromText(text) {
  if (typeof text !== 'string' || !isSubtitleUrl(text)) return [];
  const urls = text.match(/https?:\/\/[^\s"'<>]+\.(?:vtt|srt)(?:\?[^\s"'<>]*)?/gi) || [];
  if (/^\/[^/].*\.(?:vtt|srt)(?:\?|$)/i.test(text)) {
    urls.push(text);
  }
  return urls.length > 0 ? urls : (isSubtitleFileUrl(text) ? [text] : []);
}

function addSubtitleUrl(url, lang) {
  if (!isSubtitleFileUrl(url)) return;
  const absoluteUrl = normalizeDownloadUrl(url);
  const inferred = inferSubtitleLanguage({ url: absoluteUrl, lang });
  addSubtitleFiles([{
    url: absoluteUrl,
    lang: inferred.label.charAt(0).toUpperCase() + inferred.label.slice(1),
    ext: getSubtitleExtension(absoluteUrl)
  }]);
}

function addSubtitleFiles(foundSubs) {
  if (foundSubs.length > 0) {
    const byKey = new Map();

    subtitleFiles.concat(foundSubs).filter(sub => {
      return (sub.ext || getSubtitleExtension(sub.url)) === 'vtt';
    }).forEach(sub => {
      const key = getSubtitleDedupeKey(sub);
      const normalized = normalizeSubtitleFile(sub);
      const existing = byKey.get(key);

      if (!existing || (existing.lang === 'Sub' && normalized.lang !== 'Sub')) {
        byKey.set(key, normalized);
      }
    });

    const normalizedSubs = Array.from(byKey.values());
    const hasNamedSubtitle = normalizedSubs.some(sub => sub.lang !== 'Sub');
    subtitleFiles = hasNamedSubtitle ? normalizedSubs.filter(sub => sub.lang !== 'Sub') : normalizedSubs;
    console.log("[Cinemana DL Extension] Parsed subtitles:", subtitleFiles);
  }
}

function normalizeSubtitleFile(sub) {
  const inferred = inferSubtitleLanguage(sub);
  return {
    url: normalizeDownloadUrl(sub.url),
    lang: inferred.label.charAt(0).toUpperCase() + inferred.label.slice(1),
    ext: sub.ext || getSubtitleExtension(sub.url)
  };
}

function getSubtitleExtension(url) {
  const cleanUrl = String(url || '').split('?')[0].toLowerCase();
  if (cleanUrl.endsWith('.srt')) return 'srt';
  return 'vtt';
}

function getSubtitleDedupeKey(sub) {
  const inferred = inferSubtitleLanguage(sub);
  const lang = inferred.code || inferred.label.toLowerCase();
  const ext = sub.ext || getSubtitleExtension(sub.url);
  const cleanPath = getCleanSubtitlePath(sub.url);
  const transfile = cleanPath.match(/([^/]*_(?:ar|en)_transfile\.(?:vtt|srt))$/i);
  return transfile ? transfile[1].toLowerCase() : `${lang}:${ext}`;
}

function getCleanSubtitlePath(url) {
  try {
    return new URL(url, location.href).pathname.toLowerCase();
  } catch (error) {
    return String(url || '').split('?')[0].toLowerCase();
  }
}

function sanitizeFilename(str) {
  return String(str || '')
    .trim()
    .replace(/['"]/g, '')
    .replace(/[^a-z0-9\u0600-\u06FF_-]/gi, '_')
    .replace(/_+/g, '_')
    .replace(/^_+|_+$/g, '') || 'Cinemana_Video';
}

function normalizeDownloadUrl(url) {
  try {
    return new URL(String(url || '').replace(/&amp;/g, '&'), location.href).href;
  } catch (error) {
    return String(url || '').replace(/&amp;/g, '&');
  }
}

function getMovieTitle() {
  const selectors = [
    'h1',
    '.movie-title',
    '.title',
    '.video-title',
    '.movie-name',
    '.title-content h1'
  ];
  for (let selector of selectors) {
    const el = document.querySelector(selector);
    if (el && el.innerText.trim()) {
      movieTitle = sanitizeFilename(el.innerText.trim());
      break;
    }
  }

  if (movieTitle === "Cinemana_Video" && document.title && document.title.trim()) {
    movieTitle = sanitizeFilename(document.title.replace(/\s*[-|]\s*Cinemana.*$/i, '').trim());
  }

  return movieTitle;
}

function parseMediaMetadata(data) {
  if (!data) return;

  const nextMetadata = {};
  const titleKeys = new Set(['title', 'name', 'movietitle', 'moviename', 'videotitle', 'originaltitle', 'englishtitle', 'arabictitle']);
  const seriesKeys = new Set(['seriesname', 'showname', 'tvshowname', 'serialname', 'programname']);
  const seasonKeys = new Set(['season', 'seasonnumber', 'seasonno', 'seasonnum', 'seasonindex']);
  const episodeKeys = new Set(['episode', 'episodenumber', 'episodeno', 'episodenum', 'episodeindex']);

  function assignText(field, value) {
    if (nextMetadata[field] || value === null || value === undefined) return;
    const text = String(value).trim();
    if (!text || /^\d+$/.test(text) || /^https?:\/\//i.test(text)) return;
    nextMetadata[field] = text;
  }

  function assignNumber(field, value) {
    if (nextMetadata[field] || value === null || value === undefined) return;
    const match = String(value).match(/\d{1,4}/);
    if (match) nextMetadata[field] = match[0];
  }

  function walk(obj, depth = 0) {
    if (!obj || depth > 6) return;
    if (Array.isArray(obj)) {
      obj.forEach(item => walk(item, depth + 1));
      return;
    }
    if (typeof obj !== 'object') return;

    for (const key in obj) {
      if (!Object.prototype.hasOwnProperty.call(obj, key)) continue;
      const value = obj[key];
      const normalizedKey = key.replace(/[_-]/g, '').toLowerCase();

      if (seriesKeys.has(normalizedKey)) {
        assignText('seriesName', value);
      } else if (titleKeys.has(normalizedKey)) {
        assignText('title', value);
      } else if (seasonKeys.has(normalizedKey)) {
        assignNumber('seasonNumber', value);
      } else if (episodeKeys.has(normalizedKey)) {
        assignNumber('episodeNumber', value);
      }

      if (value && typeof value === 'object') {
        walk(value, depth + 1);
      }
    }
  }

  walk(data);
  mediaMetadata = { ...mediaMetadata, ...nextMetadata };
}

function parseSeasonEpisodeFromText(text) {
  const source = String(text || '').replace(/\s+/g, ' ');
  const seasonFirst = [
    /\bS(?:eason)?\s*0*(\d{1,3})\s*[-_.,:]?\s*E(?:p(?:isode)?)?\s*0*(\d{1,4})\b/i,
    /\bSeason\s*0*(\d{1,3})\b.*?\bEpisode\s*0*(\d{1,4})\b/i,
    /الموسم\s*0*(\d{1,3}).*?الحلقة\s*0*(\d{1,4})/i
  ];
  const episodeFirst = [
    /\bEpisode\s*0*(\d{1,4})\b.*?\bSeason\s*0*(\d{1,3})\b/i,
    /الحلقة\s*0*(\d{1,4}).*?الموسم\s*0*(\d{1,3})/i
  ];

  for (const pattern of seasonFirst) {
    const match = source.match(pattern);
    if (match) return { seasonNumber: match[1], episodeNumber: match[2] };
  }

  for (const pattern of episodeFirst) {
    const match = source.match(pattern);
    if (match) return { seasonNumber: match[2], episodeNumber: match[1] };
  }

  return {};
}

// Likewise, the episode carousel lists every episode, marking only the one
// currently playing with an "iswatching" class on its container, e.g.
// <div class="episode-item iswatching"> ... <p class="type">Episode 2</p>
function getActiveEpisodeElement() {
  const selectors = [
    '.episode-item.iswatching',
    '[class*="episode-item"][class*="iswatching"]',
    '[class*="episode"][class*="watching"]'
  ];
  for (const selector of selectors) {
    const el = document.querySelector(selector);
    if (el) return el;
  }
  return null;
}

function getActiveEpisodeNumber(episodeEl) {
  const el = episodeEl || getActiveEpisodeElement();
  if (!el) return null;
  const text = el.innerText || el.textContent || '';
  const match = text.match(/Episode\s*0*(\d{1,4})/i) || text.match(/الحلقة\s*0*(\d{1,4})/);
  return match ? match[1] : null;
}

// Cinemana's season selector lists every season as a plain number, marking
// only the currently selected one with an "active" class, e.g.
// <span class="season-number active">3</span>
// The page can contain more than one season block at once (mobile/desktop
// layouts, or a leftover block from a season the user previewed earlier), so
// a page-wide query can land on a stale "active" season. To avoid that, look
// first within the same season container as the episode we already found is
// actually playing, and only fall back to a page-wide search if that fails.
function getActiveSeasonNumber(episodeEl) {
  const selectors = ['.season-number.active', '[class*="season-number"][class*="active"]'];
  const el = episodeEl || getActiveEpisodeElement();
  const scope = el && (el.closest('.season-container') || el.closest('.season-info'));

  const searchIn = (root) => {
    for (const selector of selectors) {
      const match = root.querySelector(selector);
      const text = match && (match.innerText || match.textContent || '').trim();
      const num = text && text.match(/\d{1,3}/);
      if (num) return num[0];
    }
    return null;
  };

  return (scope && searchIn(scope)) || searchIn(document);
}

function getDomMediaMetadata() {
  const activeEpisodeEl = getActiveEpisodeElement();
  const activeEpisodeNumber = getActiveEpisodeNumber(activeEpisodeEl);
  const activeSeasonNumber = getActiveSeasonNumber(activeEpisodeEl);

  if (activeSeasonNumber && activeEpisodeNumber) {
    return {
      title: getMovieTitle(),
      seasonNumber: activeSeasonNumber,
      episodeNumber: activeEpisodeNumber
    };
  }

  // Fallback for pages that don't use the season-number/episode-item markup
  // above: join all season/episode-ish text on the page and pattern-match it.
  // This is best-effort only, since it can't distinguish the active season or
  // episode from other ones listed on the same page.
  const selectors = [
    'h1',
    '.movie-title',
    '.title',
    '.video-title',
    '.movie-name',
    '.title-content h1',
    '[class*="season"]',
    '[class*="episode"]',
    'cinemana-video-detail'
  ];
  const parts = [];

  selectors.forEach(selector => {
    document.querySelectorAll(selector).forEach(el => {
      const text = (el.innerText || el.textContent || '').trim();
      if (text) parts.push(text);
    });
  });

  const fallback = parseSeasonEpisodeFromText(parts.join(' '));
  return {
    title: getMovieTitle(),
    seasonNumber: activeSeasonNumber || fallback.seasonNumber,
    episodeNumber: activeEpisodeNumber || fallback.episodeNumber
  };
}

function getMediaFilenameBase() {
  const metadata = { ...getDomMediaMetadata(), ...mediaMetadata };
  const seasonNumber = metadata.seasonNumber;
  const episodeNumber = metadata.episodeNumber;
  const isSeries = Boolean(seasonNumber && episodeNumber) || /[?&]lastEpisodeVideoID=/i.test(location.href);
  const title = metadata.seriesName || metadata.title || getMovieTitle();

  if (isSeries && seasonNumber && episodeNumber) {
    return sanitizeFilename(`${title}_s${seasonNumber}_e${episodeNumber}`);
  }

  return sanitizeFilename(title);
}

function getActiveSubtitleTrack() {
  const video = document.querySelector('video');
  if (!video || !video.textTracks) return null;
  const activeTrack = Array.from(video.textTracks).find(track => track.mode === 'showing');
  return activeTrack ? activeTrack.language || activeTrack.label : null;
}

function getPageLanguage() {
  const match = location.pathname.match(/\/video\/(ar|en)\//i);
  return match ? match[1].toLowerCase() : 'en';
}

function getUiText(key) {
  const lang = getPageLanguage();
  const labels = {
    en: {
      download: 'Download',
      subtitle: 'Subtitle',
      videoQuality: 'Video Quality',
      subtitles: 'Subtitles',
      waitingVideo: 'Play the video to capture download links.',
      waitingSubtitle: 'Play the video to capture subtitle links.',
      noVideo: 'Please play the video first so we can capture the streaming URL!',
      noSubtitle: 'Please play the video first so we can capture the subtitle URL!',
      defaultBadge: 'Default',
      activeBadge: 'Running'
    },
    ar: {
      download: '\u062a\u062d\u0645\u064a\u0644',
      subtitle: '\u062a\u0631\u062c\u0645\u0629',
      videoQuality: '\u062c\u0648\u062f\u0629 \u0627\u0644\u0641\u064a\u062f\u064a\u0648',
      subtitles: '\u0627\u0644\u062a\u0631\u062c\u0645\u0627\u062a',
      waitingVideo: '\u0634\u063a\u0644 \u0627\u0644\u0641\u064a\u062f\u064a\u0648 \u0644\u0627\u0644\u062a\u0642\u0627\u0637 \u0631\u0648\u0627\u0628\u0637 \u0627\u0644\u062a\u062d\u0645\u064a\u0644.',
      waitingSubtitle: '\u0634\u063a\u0644 \u0627\u0644\u0641\u064a\u062f\u064a\u0648 \u0644\u0627\u0644\u062a\u0642\u0627\u0637 \u0631\u0648\u0627\u0628\u0637 \u0627\u0644\u062a\u0631\u062c\u0645\u0629.',
      noVideo: '\u0634\u063a\u0644 \u0627\u0644\u0641\u064a\u062f\u064a\u0648 \u0623\u0648\u0644\u0627 \u062d\u062a\u0649 \u0646\u0644\u062a\u0642\u0637 \u0631\u0627\u0628\u0637 \u0627\u0644\u062a\u062d\u0645\u064a\u0644!',
      noSubtitle: '\u0634\u063a\u0644 \u0627\u0644\u0641\u064a\u062f\u064a\u0648 \u0623\u0648\u0644\u0627 \u062d\u062a\u0649 \u0646\u0644\u062a\u0642\u0637 \u0631\u0627\u0628\u0637 \u0627\u0644\u062a\u0631\u062c\u0645\u0629!',
      defaultBadge: '\u0627\u0641\u062a\u0631\u0627\u0636\u064a',
      activeBadge: '\u0645\u0634\u063a\u0644\u0629'
    }
  };

  return (labels[lang] || labels.en)[key] || labels.en[key];
}

function inferSubtitleLanguage(sub) {
  const cleanPath = getCleanSubtitlePath(sub.url);
  const source = `${sub.lang || ''} ${cleanPath}`.toLowerCase();
  const fileName = cleanPath.split('/').pop() || '';

  if (/(^|[_-])ar(?:[_-]transfile)?\.(?:vtt|srt)$/i.test(fileName) || /(?:^|[_/-])ar(?:[_./-]|$)|arabic|\u0639\u0631\u0628/.test(source)) {
    return { code: 'ar', label: 'Arabic' };
  }
  if (/(^|[_-])en(?:[_-]transfile)?\.(?:vtt|srt)$/i.test(fileName) || /(?:^|[_/-])en(?:[_./-]|$)|english/.test(source)) {
    return { code: 'en', label: 'English' };
  }
  return { code: '', label: sub.lang || 'Sub' };
}

function getPreferredSubtitle() {
  const pageLang = getPageLanguage();
  const activeLang = getActiveSubtitleTrack();

  if (activeLang) {
    const activeMatch = subtitleFiles.find(sub => {
      const inferred = inferSubtitleLanguage(sub);
      return sub.lang.toLowerCase().includes(activeLang.toLowerCase()) ||
        activeLang.toLowerCase().includes(sub.lang.toLowerCase()) ||
        inferred.code === activeLang.toLowerCase();
    });
    if (activeMatch) return activeMatch;
  }

  return subtitleFiles.find(sub => inferSubtitleLanguage(sub).code === pageLang) ||
    subtitleFiles.find(sub => inferSubtitleLanguage(sub).code === 'ar') ||
    subtitleFiles[0];
}

// UI Injection logic
function injectDownloadButton() {
  if (!document.body) {
    return;
  }

  // Only inject on video details/player pages
  if (!location.href.includes('/video/')) {
    const oldContainer = document.getElementById('cinemana-download-container');
    if (oldContainer) {
      oldContainer.remove();
    }
    return;
  }

  const target = findCinemanaVideoStat();
  const existingContainer = document.getElementById('cinemana-download-container');

  if (!target) {
    return;
  }

  applyStatLayoutClasses(target);

  if (existingContainer) {
    existingContainer.classList.remove('cinemana-dl-floating');
    if (placeDownloadContainerNearStats(target, existingContainer)) {
      console.log("[Cinemana DL Extension] Moved download button into video stat:", target);
    }
    return;
  }

  console.log("[Cinemana DL Extension] Injecting download button into video stat:", target);

  const container = document.createElement('div');
  container.id = 'cinemana-download-container';
  container.className = 'cinemana-dl-container';
  
  container.innerHTML = `
    <div class="cinemana-dl-group">
      <button id="cinemana-dl-main-btn" class="cinemana-dl-btn cinemana-dl-btn-main">
        <svg class="dl-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
          <polyline points="7 10 12 15 17 10"></polyline>
          <line x1="12" y1="15" x2="12" y2="3"></line>
        </svg>
        <span data-i18n="download">${getUiText('download')}</span>
      </button>
      <button id="cinemana-dl-video-toggle" class="cinemana-dl-btn cinemana-dl-btn-toggle">
        <svg class="arrow-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <polyline points="6 9 12 15 18 9"></polyline>
        </svg>
      </button>
      <div id="cinemana-dl-video-menu" class="cinemana-dl-menu">
        <div class="menu-loading">${getUiText('waitingVideo')}</div>
      </div>
    </div>
    <div class="cinemana-dl-group">
      <button id="cinemana-dl-subtitle-btn" class="cinemana-dl-btn cinemana-dl-btn-main">
        <svg class="subtitle-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"></path>
          <path d="M8 9h8"></path>
          <path d="M8 13h5"></path>
        </svg>
        <span data-i18n="subtitle">${getUiText('subtitle')}</span>
      </button>
      <button id="cinemana-dl-subtitle-toggle" class="cinemana-dl-btn cinemana-dl-btn-toggle">
        <svg class="arrow-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <polyline points="6 9 12 15 18 9"></polyline>
        </svg>
      </button>
      <div id="cinemana-dl-subtitle-menu" class="cinemana-dl-menu">
        <div class="menu-loading">${getUiText('waitingSubtitle')}</div>
      </div>
    </div>
  `;

  placeDownloadContainerNearStats(target, container);

  const mainBtn = container.querySelector('#cinemana-dl-main-btn');
  const subtitleBtn = container.querySelector('#cinemana-dl-subtitle-btn');
  const videoToggleBtn = container.querySelector('#cinemana-dl-video-toggle');
  const subtitleToggleBtn = container.querySelector('#cinemana-dl-subtitle-toggle');
  const videoMenu = container.querySelector('#cinemana-dl-video-menu');
  const subtitleMenu = container.querySelector('#cinemana-dl-subtitle-menu');

  mainBtn.addEventListener('click', handleMainDownload);
  subtitleBtn.addEventListener('click', handleSubtitleDownload);
  videoToggleBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    subtitleMenu.classList.remove('show');
    videoMenu.classList.toggle('show');
  });
  subtitleToggleBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    videoMenu.classList.remove('show');
    subtitleMenu.classList.toggle('show');
  });

  document.addEventListener('click', () => {
    videoMenu.classList.remove('show');
    subtitleMenu.classList.remove('show');
  });

  videoMenu.addEventListener('click', (e) => {
    e.stopPropagation();
  });

  subtitleMenu.addEventListener('click', (e) => {
    e.stopPropagation();
  });

  updateDropdownUI();
}

function applyStatLayoutClasses(target) {
  target.classList.add('cinemana-dl-stat-target');

  const statRow = target.parentElement;
  if (statRow) {
    statRow.classList.add('cinemana-dl-stat-row');
  }
}

function placeDownloadContainerNearStats(target, container) {
  const anchor = findStatControlsAnchor(target);

  if (anchor && anchor.parentElement) {
    anchor.parentElement.classList.add('cinemana-dl-inline-host');

    if (container.previousElementSibling === anchor && container.parentElement === anchor.parentElement) {
      return false;
    }

    anchor.insertAdjacentElement('afterend', container);
    return true;
  }

  if (container.parentElement !== target) {
    target.appendChild(container);
    return true;
  }

  return false;
}

function findStatControlsAnchor(target) {
  const controls = Array.from(target.querySelectorAll('button, a, [role="button"]')).filter(el => {
    return !el.closest('#cinemana-download-container') && isVisibleElement(el);
  });

  if (controls.length === 0) {
    return Array.from(target.children).find(child => {
      return child.id !== 'cinemana-download-container' && isVisibleElement(child);
    }) || null;
  }

  if (controls.length === 1) {
    return controls[0];
  }

  const commonParent = getLowestCommonAncestor(controls[0], controls[1], target);
  return commonParent && commonParent !== target ? commonParent : controls[1];
}

function getLowestCommonAncestor(first, second, limit) {
  const ancestors = new Set();
  let node = first;

  while (node && node !== limit.parentElement) {
    ancestors.add(node);
    if (node === limit) break;
    node = node.parentElement;
  }

  node = second;
  while (node && node !== limit.parentElement) {
    if (ancestors.has(node)) {
      return node;
    }
    if (node === limit) break;
    node = node.parentElement;
  }

  return null;
}

function isVisibleElement(el) {
  return !!(el && (el.offsetWidth > 0 || el.offsetHeight > 0 || el.getClientRects().length > 0));
}

function findInjectionTarget() {
  const statTarget = findCinemanaVideoStat();
  if (statTarget) {
    return statTarget;
  }

  const possibleSelectors = [
    '[class*="like"]',
    '[class*="thumb"]',
    '[class*="vote"]',
    '[class*="rating"]',
    '[id*="like"]',
    '[aria-label*="like" i]',
    '[aria-label*="dislike" i]',
    '[title*="like" i]',
    '[title*="dislike" i]',
    'button',
    'a'
  ];

  for (let selector of possibleSelectors) {
    const elements = document.querySelectorAll(selector);
    for (let el of elements) {
      const text = el.innerText || '';
      const label = `${el.getAttribute('aria-label') || ''} ${el.getAttribute('title') || ''}`;
      if (
        /like/i.test(text) || 
        /like|dislike/i.test(label) ||
        /thumb|vote|rating/i.test(el.className || '') ||
        /اعجبني/i.test(text) || 
        /أعجبني/i.test(text) || 
        el.querySelector('svg[class*="like"]') || 
        el.querySelector('svg[class*="thumb"]') || 
        el.querySelector('[class*="thumb"]') || 
        el.querySelector('.fa-thumbs-up') ||
        (el.innerText && (el.innerText.includes('👍') || el.innerText.includes('Like')))
      ) {
        if (
          el.id !== 'cinemana-dl-main-btn' &&
          el.id !== 'cinemana-dl-subtitle-btn' &&
          el.id !== 'cinemana-dl-video-toggle' &&
          el.id !== 'cinemana-dl-subtitle-toggle' &&
          el.offsetWidth > 0
        ) {
          return el;
        }
      }
    }
  }

  const actionContainers = [
    '.movie-actions',
    '.video-actions',
    '.buttons-container',
    '.interaction-bar',
    '.rating-btns',
    '.video-details',
    '.movie-details'
  ];
  for (let selector of actionContainers) {
    const el = document.querySelector(selector);
    if (el) return el;
  }

  const titleSelectors = ['.title-content', '.movie-title', 'h1'];
  for (let selector of titleSelectors) {
    const el = document.querySelector(selector);
    if (el) return el;
  }

  const playerSelectors = ['video', '.video-js', '#player', '.player-container'];
  for (let selector of playerSelectors) {
    const el = document.querySelector(selector);
    if (el) return el;
  }

  return null;
}

function findCinemanaVideoStat() {
  const exactXpath = '/html/body/app/vertical-layout/div/div/div/div/content/video-page/div/div[1]/cinemana-video/div/div/cinemana-video-detail/div/div[1]/div/cinemana-video-stat';
  const exactResult = document.evaluate(exactXpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
  if (exactResult.singleNodeValue) {
    return exactResult.singleNodeValue;
  }

  const directTarget = document.querySelector('#container-3 content video-page cinemana-video-stat, cinemana-video-stat');
  if (directTarget) {
    return directTarget;
  }

  const xpath = '//*[@id="container-3"]/content/video-page/div/div[1]/cinemana-video/div/div/cinemana-video-detail/div/div[1]/div/cinemana-video-stat';
  const result = document.evaluate(xpath, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
  return result.singleNodeValue;
}

function getInsertionAnchor(target) {
  if (target.matches('button, a')) {
    return target.closest('[class*="like"], [class*="thumb"], [class*="vote"], [class*="rating"], [class*="action"]') || target.parentElement || target;
  }

  return target;
}

function updateDropdownUI() {
  const videoMenu = document.getElementById('cinemana-dl-video-menu');
  const subtitleMenu = document.getElementById('cinemana-dl-subtitle-menu');
  const downloadLabel = document.querySelector('[data-i18n="download"]');
  const subtitleLabel = document.querySelector('[data-i18n="subtitle"]');
  if (!videoMenu || !subtitleMenu) return;

  if (downloadLabel) downloadLabel.textContent = getUiText('download');
  if (subtitleLabel) subtitleLabel.textContent = getUiText('subtitle');
  const mainBtn = document.getElementById('cinemana-dl-main-btn');
  const subtitleBtn = document.getElementById('cinemana-dl-subtitle-btn');
  if (mainBtn) mainBtn.title = getUiText('download');
  if (subtitleBtn) subtitleBtn.title = getUiText('subtitle');

  if (videoFiles.length === 0) {
    videoMenu.innerHTML = `<div class="menu-loading">${getUiText('waitingVideo')}</div>`;
  } else {
    let videoHtml = `<div class="menu-section"><div class="menu-section-title">${getUiText('videoQuality')}</div>`;
    videoFiles.forEach((file, index) => {
      videoHtml += `
        <div class="menu-item download-video" data-index="${index}">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="16" height="16">
            <rect x="2" y="2" width="20" height="20" rx="2.18" ry="2.18"></rect>
            <line x1="7" y1="2" x2="7" y2="22"></line>
            <line x1="17" y1="2" x2="17" y2="22"></line>
            <line x1="2" y1="12" x2="22" y2="12"></line>
            <line x1="2" y1="7" x2="7" y2="7"></line>
            <line x1="2" y1="17" x2="7" y2="17"></line>
            <line x1="17" y1="17" x2="22" y2="17"></line>
            <line x1="17" y1="7" x2="22" y2="7"></line>
          </svg>
          <span>${file.quality} (.${file.type})</span>
        </div>
      `;
    });
    videoHtml += `</div>`;
    videoMenu.innerHTML = videoHtml;
  }

  if (subtitleFiles.length === 0) {
    subtitleMenu.innerHTML = `<div class="menu-loading">${getUiText('waitingSubtitle')}</div>`;
  } else {
    const activeLang = getActiveSubtitleTrack();
    const preferredSub = getPreferredSubtitle();
    let subtitleHtml = `<div class="menu-section"><div class="menu-section-title">${getUiText('subtitles')}</div>`;

    subtitleFiles.forEach((sub, index) => {
      const inferred = inferSubtitleLanguage(sub);
      const langLabel = inferred.label;
      const ext = sub.ext || getSubtitleExtension(sub.url);
      const isActive = activeLang && (
        sub.lang.toLowerCase().includes(activeLang.toLowerCase()) ||
        activeLang.toLowerCase().includes(sub.lang.toLowerCase()) ||
        inferred.code === activeLang.toLowerCase()
      );
      const isDefault = preferredSub && preferredSub.url === sub.url;
      subtitleHtml += `
        <div class="menu-item download-sub" data-index="${index}">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="16" height="16">
            <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"></path>
          </svg>
          <span>${langLabel} (.${ext})</span>
          ${isActive ? `<span class="active-badge">${getUiText('activeBadge')}</span>` : ''}
          ${!isActive && isDefault ? `<span class="active-badge">${getUiText('defaultBadge')}</span>` : ''}
        </div>
      `;
    });
    subtitleHtml += `</div>`;
    subtitleMenu.innerHTML = subtitleHtml;
  }

  videoMenu.querySelectorAll('.download-video').forEach(el => {
    el.addEventListener('click', function() {
      const idx = parseInt(this.getAttribute('data-index'));
      triggerDownloadVideo(videoFiles[idx]);
    });
  });

  subtitleMenu.querySelectorAll('.download-sub').forEach(el => {
    el.addEventListener('click', function() {
      const idx = parseInt(this.getAttribute('data-index'));
      triggerDownloadSubtitle(subtitleFiles[idx]);
    });
  });
}

function handleMainDownload() {
  if (videoFiles.length === 0) {
    alert(getUiText('noVideo'));
    return;
  }

  let bestVideo = videoFiles[0];
  
  videoFiles.forEach(file => {
    const resA = getVideoQualityRank(file.quality);
    const resB = getVideoQualityRank(bestVideo.quality);
    if (resA > resB) {
      bestVideo = file;
    }
  });

  triggerDownloadVideo(bestVideo);
}

function handleSubtitleDownload() {
  if (subtitleFiles.length === 0) {
    alert(getUiText('noSubtitle'));
    return;
  }

  triggerDownloadSubtitle(getPreferredSubtitle());
}

function triggerDownloadVideo(videoFile) {
  const base = getMediaFilenameBase();
  const filename = `${base}.${videoFile.type}`;
  sendDownloadMessage({
    action: 'download',
    url: normalizeDownloadUrl(videoFile.url),
    filename: filename
  });
}

async function triggerDownloadSubtitle(subFile) {
  const filename = `${getMediaFilenameBase()}.vtt`;
  console.log('[Cinemana DL Extension] Downloading subtitle as:', filename, subFile.url);

  sendDownloadMessage({
    action: 'downloadSubtitle',
    url: normalizeDownloadUrl(subFile.url),
    filename: filename,
    pageTitle: document.title || '',
    pageUrl: location.href
  }, response => {
    if (!response || !response.ok) {
      alert((response && response.error) || getUiText('noSubtitle'));
    }
  });
}

// Watch for DOM changes to inject button
const observer = new MutationObserver((mutations) => {
  if (injectQueued) return;
  injectQueued = true;
  requestAnimationFrame(() => {
    injectQueued = false;
    injectDownloadButton();
  });
});

function startDomObserver() {
  const observerRoot = document.documentElement || document.body;
  if (!observerRoot) {
    return;
  }

  observer.observe(observerRoot, {
    childList: true,
    subtree: true
  });

  injectDownloadButton();
}

if (document.documentElement || document.body) {
  startDomObserver();
} else {
  document.addEventListener('DOMContentLoaded', startDomObserver, { once: true });
}