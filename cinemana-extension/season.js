// Season / series batch download for Cinemana Download Button.
// Runs in the same isolated world as content.js, so it reuses its helpers
// (parseVideoFiles-style search, getVideoQualityRank, sanitizeFilename, ...).

const seasonState = {
  episodes: new Map(),       // episodeId -> { id, season, episode, title }
  videoTemplates: new Set(), // API URLs with the episode id replaced by {id}
  subtitleTemplates: new Set(),
  running: false,
  cancel: false
};

const API_BASE = 'https://cinemana.shabakaty.com/api/android';

function seasonText(key) {
  const ar = {
    button: 'الموسم',
    title: 'تحميل حلقات المسلسل',
    season: 'الموسم',
    all: 'كل المواسم',
    quality: 'الجودة',
    best: 'أعلى جودة',
    subtitle: 'الترجمة (.srt)',
    arabic: 'عربي',
    english: 'إنجليزي',
    none: 'بدون ترجمة',
    start: 'تحميل الحلقات المحددة',
    stop: 'إيقاف',
    loading: 'جاري جلب قائمة الحلقات...',
    noEpisodes: 'لم أجد حلقات. شغّل أي حلقة من المسلسل ثم أعد فتح هذه النافذة.',
    episode: 'الحلقة',
    done: 'تم إرسال جميع الحلقات إلى التحميلات.',
    noVideo: 'لم أجد رابط فيديو',
    noSub: 'لا توجد ترجمة',
    selectAll: 'تحديد الكل'
  };
  const en = {
    button: 'Season',
    title: 'Download series episodes',
    season: 'Season',
    all: 'All seasons',
    quality: 'Quality',
    best: 'Best',
    subtitle: 'Subtitle (.srt)',
    arabic: 'Arabic',
    english: 'English',
    none: 'No subtitle',
    start: 'Download selected episodes',
    stop: 'Stop',
    loading: 'Loading episode list...',
    noEpisodes: 'No episodes found. Play any episode of the series, then reopen this panel.',
    episode: 'Episode',
    done: 'All episodes were sent to downloads.',
    noVideo: 'No video link found',
    noSub: 'No subtitle',
    selectAll: 'Select all'
  };
  const lang = typeof getPageLanguage === 'function' ? getPageLanguage() : 'en';
  return (lang === 'ar' ? ar : en)[key];
}

// ---------- Learning from intercepted API traffic ----------

window.addEventListener('message', event => {
  if (event.source !== window || !event.data || event.data.type !== 'CINEMANA_API_DATA') return;
  const { url, data, pageUrl } = event.data;
  collectEpisodes(data);
  learnTemplates(url, data, pageUrl);
});

function pickField(obj, keys) {
  for (const key of keys) {
    if (obj[key] !== undefined && obj[key] !== null && obj[key] !== '') return obj[key];
  }
  return undefined;
}

function collectEpisodes(data, depth = 0) {
  if (!data || depth > 6) return;
  if (Array.isArray(data)) {
    data.forEach(item => collectEpisodes(item, depth + 1));
    return;
  }
  if (typeof data !== 'object') return;

  const id = pickField(data, ['nb', 'videoId', 'videoID', 'video_id', 'id']);
  const episode = pickField(data, ['episodeNummer', 'episodeNumber', 'episodeNum', 'episode_number', 'episode']);
  const season = pickField(data, ['season', 'seasonNumber', 'seasonNum', 'season_number']);
  if (id && /^\d+$/.test(String(id)) && episode !== undefined && /^\d+$/.test(String(episode))) {
    seasonState.episodes.set(String(id), {
      id: String(id),
      season: parseInt(season, 10) || 1,
      episode: parseInt(episode, 10),
      title: pickField(data, ['ar_title', 'en_title', 'title', 'name']) || ''
    });
  }

  for (const key in data) {
    if (Object.prototype.hasOwnProperty.call(data, key) && data[key] && typeof data[key] === 'object') {
      collectEpisodes(data[key], depth + 1);
    }
  }
}

function learnTemplates(url, data, pageUrl) {
  const videoKey = getVideoKeyFromPageUrl(pageUrl || location.href);
  if (!videoKey || typeof url !== 'string' || !url.includes(videoKey)) return;
  if (isSubtitleFileUrl(url)) return;

  let absolute;
  try {
    absolute = new URL(url, location.href).href;
  } catch (e) {
    return;
  }
  const template = absolute.split(videoKey).join('{id}');

  if (extractVideoList(data).length > 0) seasonState.videoTemplates.add(template);
  if (extractSubtitleList(data).length > 0) seasonState.subtitleTemplates.add(template);
}

// ---------- Generic extractors (non-mutating versions of content.js parsers) ----------

function extractVideoList(data) {
  const found = [];
  (function search(obj) {
    if (!obj || typeof obj !== 'object') return;
    if (Array.isArray(obj)) return obj.forEach(search);
    const url = obj.videoUrl || obj.fileUrl || obj.url || obj.link;
    if (typeof url === 'string' && /\.(mp4|mkv)(\?|$)/i.test(url)) {
      found.push({
        url: normalizeDownloadUrl(url),
        quality: normalizeVideoQuality(obj.resolution || obj.quality || obj.label || obj.name || url),
        type: /\.mkv(\?|$)/i.test(url) ? 'mkv' : (obj.container || 'mp4')
      });
      return;
    }
    for (const key in obj) {
      if (Object.prototype.hasOwnProperty.call(obj, key)) search(obj[key]);
    }
  })(data);
  return found.sort((a, b) => getVideoQualityRank(b.quality) - getVideoQualityRank(a.quality));
}

function extractSubtitleList(data) {
  const found = [];
  (function search(obj) {
    if (!obj) return;
    if (typeof obj === 'string') {
      if (isSubtitleFileUrl(obj)) found.push({ url: normalizeDownloadUrl(obj), lang: '' });
      return;
    }
    if (typeof obj !== 'object') return;
    if (Array.isArray(obj)) return obj.forEach(search);
    const url = obj.file || obj.fileUrl || obj.url || obj.link || obj.srt || obj.vtt;
    if (typeof url === 'string' && isSubtitleFileUrl(url)) {
      found.push({
        url: normalizeDownloadUrl(url),
        lang: obj.lang || obj.language || obj.type || obj.name || obj.title || obj.label || ''
      });
      return;
    }
    for (const key in obj) {
      if (Object.prototype.hasOwnProperty.call(obj, key)) search(obj[key]);
    }
  })(data);
  return found.map(sub => ({ ...sub, code: inferSubtitleLanguage(sub).code }));
}

// ---------- Fetching ----------

async function fetchJson(url) {
  const response = await fetch(url, { credentials: 'include' });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.json();
}

async function tryTemplates(templates, id, extractor) {
  for (const template of templates) {
    try {
      const list = extractor(await fetchJson(template.split('{id}').join(id)));
      if (list.length > 0) return list;
    } catch (e) {
      console.warn('[Cinemana DL Extension] Request failed:', template, e);
    }
  }
  return [];
}

function getSeriesIdCandidates() {
  const ids = new Set();
  const pathMatch = location.pathname.match(/\/video\/(?:ar|en)\/(\d+)/i);
  if (pathMatch) ids.add(pathMatch[1]);
  const episodeId = new URL(location.href).searchParams.get('lastEpisodeVideoID');
  if (episodeId) ids.add(episodeId);
  return Array.from(ids);
}

async function loadEpisodeList() {
  if (seasonState.episodes.size > 1) return;

  for (const id of getSeriesIdCandidates()) {
    try {
      // The episode's info names its root series; the series id lists all episodes.
      const info = await fetchJson(`${API_BASE}/allVideoInfo/id/${id}`).catch(() => null);
      const rootId = info && (info.rootSeries || info.rootSeriesId) ? String(info.rootSeries || info.rootSeriesId) : id;
      collectEpisodes(await fetchJson(`${API_BASE}/videoSeason/id/${rootId}`));
      if (seasonState.episodes.size > 1) return;
    } catch (e) {
      console.warn('[Cinemana DL Extension] Could not load episode list for', id, e);
    }
  }
}

function getEpisodeVideos(id) {
  return tryTemplates(
    [...seasonState.videoTemplates, `${API_BASE}/transcoddedFiles/id/{id}`, `${API_BASE}/videoFiles/id/{id}`],
    id,
    extractVideoList
  );
}

function getEpisodeSubtitles(id) {
  return tryTemplates(
    [...seasonState.subtitleTemplates, `${API_BASE}/allVideoInfo/id/{id}`, `${API_BASE}/translationFiles/id/{id}`],
    id,
    extractSubtitleList
  );
}

function pickVideo(videos, quality) {
  if (quality === 'best') return videos[0];
  const target = getVideoQualityRank(quality);
  // Exact match, otherwise the highest quality not above the requested one.
  return videos.find(v => getVideoQualityRank(v.quality) === target) ||
    videos.find(v => getVideoQualityRank(v.quality) < target) ||
    videos[videos.length - 1];
}

function pickSubtitle(subs, lang) {
  if (lang === 'none' || subs.length === 0) return null;
  const byLang = subs.filter(sub => sub.code === lang);
  const pool = byLang.length > 0 ? byLang : subs;
  return pool.find(sub => /\.srt(\?|$)/i.test(sub.url)) || pool[0];
}

// ---------- UI ----------

function getSeriesName() {
  const metadata = { ...getDomMediaMetadata(), ...mediaMetadata };
  return sanitizeFilename(metadata.seriesName || metadata.title || getMovieTitle());
}

function pad(num) {
  return String(num).padStart(2, '0');
}

function injectSeasonButton() {
  const container = document.getElementById('cinemana-download-container');
  if (!container || container.querySelector('#cinemana-dl-season-btn')) return;
  if (!/[?&]lastEpisodeVideoID=/i.test(location.href) && !document.querySelector('[class*="episode-item"]')) return;

  const group = document.createElement('div');
  group.className = 'cinemana-dl-group';
  group.innerHTML = `
    <button id="cinemana-dl-season-btn" class="cinemana-dl-btn cinemana-dl-btn-main cinemana-dl-btn-solo">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
        <rect x="3" y="4" width="18" height="4" rx="1"></rect>
        <rect x="3" y="10" width="18" height="4" rx="1"></rect>
        <rect x="3" y="16" width="18" height="4" rx="1"></rect>
      </svg>
      <span>${seasonText('button')}</span>
    </button>`;
  container.appendChild(group);
  group.querySelector('button').addEventListener('click', event => {
    event.stopPropagation();
    openSeasonPanel();
  });
}

async function openSeasonPanel() {
  let overlay = document.getElementById('cinemana-season-overlay');
  if (overlay) overlay.remove();

  overlay = document.createElement('div');
  overlay.id = 'cinemana-season-overlay';
  overlay.dir = getPageLanguage() === 'ar' ? 'rtl' : 'ltr';
  overlay.innerHTML = `
    <div class="cinemana-season-panel">
      <div class="cs-header">
        <strong>${seasonText('title')}</strong>
        <button class="cs-close" type="button">✕</button>
      </div>
      <div class="cs-body"><div class="cs-status">${seasonText('loading')}</div></div>
    </div>`;
  document.body.appendChild(overlay);
  overlay.querySelector('.cs-close').addEventListener('click', () => {
    if (!seasonState.running) overlay.remove();
  });
  overlay.addEventListener('click', event => {
    if (event.target === overlay && !seasonState.running) overlay.remove();
  });

  await loadEpisodeList();
  renderSeasonPanel(overlay.querySelector('.cs-body'));
}

function renderSeasonPanel(body) {
  const episodes = Array.from(seasonState.episodes.values())
    .sort((a, b) => a.season - b.season || a.episode - b.episode);

  if (episodes.length === 0) {
    body.innerHTML = `<div class="cs-status">${seasonText('noEpisodes')}</div>`;
    return;
  }

  const seasons = [...new Set(episodes.map(e => e.season))];
  const currentSeason = parseInt(getActiveSeasonNumber(), 10);
  const defaultSeason = seasons.includes(currentSeason) ? currentSeason : seasons[0];
  const defaultLang = getPageLanguage() === 'en' ? 'en' : 'ar';

  body.innerHTML = `
    <div class="cs-row">
      <label>${seasonText('season')}
        <select class="cs-season">
          ${seasons.map(s => `<option value="${s}" ${s === defaultSeason ? 'selected' : ''}>${s}</option>`).join('')}
          <option value="all">${seasonText('all')}</option>
        </select>
      </label>
      <label>${seasonText('quality')}
        <select class="cs-quality">
          <option value="best">${seasonText('best')}</option>
          <option value="1080p">1080p</option>
          <option value="720p">720p</option>
          <option value="480p">480p</option>
          <option value="360p">360p</option>
        </select>
      </label>
      <label>${seasonText('subtitle')}
        <select class="cs-lang">
          <option value="ar" ${defaultLang === 'ar' ? 'selected' : ''}>${seasonText('arabic')}</option>
          <option value="en" ${defaultLang === 'en' ? 'selected' : ''}>${seasonText('english')}</option>
          <option value="none">${seasonText('none')}</option>
        </select>
      </label>
    </div>
    <label class="cs-select-all"><input type="checkbox" checked> ${seasonText('selectAll')}</label>
    <div class="cs-list"></div>
    <div class="cs-actions">
      <button type="button" class="cs-start">${seasonText('start')}</button>
    </div>
    <div class="cs-log"></div>`;

  const list = body.querySelector('.cs-list');
  const seasonSelect = body.querySelector('.cs-season');
  const selectAll = body.querySelector('.cs-select-all input');

  function renderList() {
    const value = seasonSelect.value;
    const shown = episodes.filter(e => value === 'all' || e.season === parseInt(value, 10));
    list.innerHTML = shown.map(e => `
      <label class="cs-item">
        <input type="checkbox" value="${e.id}" checked>
        S${pad(e.season)}E${pad(e.episode)} — ${seasonText('episode')} ${e.episode}
        <span class="cs-item-status" data-id="${e.id}"></span>
      </label>`).join('');
    selectAll.checked = true;
  }

  seasonSelect.addEventListener('change', renderList);
  selectAll.addEventListener('change', () => {
    list.querySelectorAll('input').forEach(input => { input.checked = selectAll.checked; });
  });
  renderList();

  const startBtn = body.querySelector('.cs-start');
  startBtn.addEventListener('click', () => {
    if (seasonState.running) {
      seasonState.cancel = true;
      return;
    }
    const ids = Array.from(list.querySelectorAll('input:checked')).map(input => input.value);
    const chosen = ids.map(id => seasonState.episodes.get(id)).filter(Boolean);
    runBatch(chosen, {
      quality: body.querySelector('.cs-quality').value,
      lang: body.querySelector('.cs-lang').value
    }, body, startBtn);
  });
}

async function runBatch(episodes, options, body, startBtn) {
  if (episodes.length === 0) return;
  seasonState.running = true;
  seasonState.cancel = false;
  startBtn.textContent = seasonText('stop');
  body.querySelectorAll('select, .cs-list input, .cs-select-all input').forEach(el => { el.disabled = true; });

  const log = body.querySelector('.cs-log');
  const series = getSeriesName();

  for (const ep of episodes) {
    if (seasonState.cancel) break;
    const status = body.querySelector(`.cs-item-status[data-id="${ep.id}"]`);
    const setStatus = text => { if (status) status.textContent = text; };
    setStatus('⏳');

    const base = `${series}_S${pad(ep.season)}E${pad(ep.episode)}`;
    const folder = `${series}/Season_${pad(ep.season)}`;

    try {
      const videos = await getEpisodeVideos(ep.id);
      const video = videos.length > 0 ? pickVideo(videos, options.quality) : null;
      if (!video) {
        setStatus(`❌ ${seasonText('noVideo')}`);
        continue;
      }

      const subs = options.lang === 'none' ? [] : await getEpisodeSubtitles(ep.id);
      const sub = pickSubtitle(subs, options.lang);

      const response = await sendBatchMessage({
        action: 'batchDownloadEpisode',
        video: { url: video.url, filename: `${folder}/${base}.${video.type}` },
        subtitle: sub ? { url: sub.url, filename: `${folder}/${base}.srt` } : null
      });

      if (response && response.ok) {
        setStatus(`✔ ${video.quality}${sub ? ' + srt' : (options.lang === 'none' ? '' : ` (${seasonText('noSub')})`)}`);
      } else {
        setStatus(`❌ ${(response && response.error) || ''}`);
      }
    } catch (error) {
      setStatus(`❌ ${error.message || error}`);
    }

    // Space out requests so the site isn't hit all at once.
    await new Promise(resolve => setTimeout(resolve, 700));
  }

  seasonState.running = false;
  startBtn.textContent = seasonText('start');
  body.querySelectorAll('select, .cs-list input, .cs-select-all input').forEach(el => { el.disabled = false; });
  if (!seasonState.cancel) log.textContent = seasonText('done');
}

function sendBatchMessage(payload) {
  return new Promise(resolve => {
    const runtime = getExtensionRuntime();
    if (!runtime) {
      alertExtensionContextInvalidated();
      resolve({ ok: false, error: 'Extension reloaded' });
      return;
    }
    runtime.sendMessage(payload, response => {
      if (chrome.runtime.lastError) {
        resolve({ ok: false, error: chrome.runtime.lastError.message });
      } else {
        resolve(response);
      }
    });
  });
}

setInterval(injectSeasonButton, 1000);
