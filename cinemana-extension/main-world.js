// Cinemana API interceptor running in the MAIN world
(function() {
  if (window.__cinemanaDlInterceptorInstalled) {
    return;
  }

  window.__cinemanaDlInterceptorInstalled = true;

  function isSubtitleUrl(url) {
    return typeof url === 'string' && /(\.vtt|\.srt|subtitle|subtitles|translation|transfile)/i.test(url);
  }

  function isJsonResponse(response) {
    try {
      return /json/i.test(response.headers.get('content-type') || '');
    } catch (e) {
      return false;
    }
  }

  function isJsonXhr(xhr) {
    try {
      return /json/i.test(xhr.getResponseHeader('content-type') || '');
    } catch (e) {
      return false;
    }
  }

  function postApiData(url, data, pageUrl) {
    window.postMessage({
      type: 'CINEMANA_API_DATA',
      url: url,
      data: data,
      pageUrl: pageUrl || location.href
    }, '*');
  }

  function postLocationChanged() {
    window.postMessage({
      type: 'CINEMANA_LOCATION_CHANGED',
      url: location.href
    }, '*');
  }

  const originalPushState = history.pushState;
  const originalReplaceState = history.replaceState;

  history.pushState = function(...args) {
    const result = originalPushState.apply(this, args);
    postLocationChanged();
    return result;
  };

  history.replaceState = function(...args) {
    const result = originalReplaceState.apply(this, args);
    postLocationChanged();
    return result;
  };

  window.addEventListener('popstate', postLocationChanged);
  window.addEventListener('hashchange', postLocationChanged);

  const originalFetch = window.fetch;
  window.fetch = async function(...args) {
    const requestPageUrl = location.href;
    const response = await originalFetch.apply(this, args);
    const url = (args[0] && typeof args[0] === 'object') ? args[0].url : String(args[0] || '');
    
    if (url && typeof url === 'string') {
      if (isSubtitleUrl(url)) {
        postApiData(url, { url: url }, requestPageUrl);
      }

      if (!isJsonResponse(response)) {
        return response;
      }

      try {
        const clone = response.clone();
        clone.json().then(data => {
          postApiData(url, data, requestPageUrl);
        }).catch(() => {});
      } catch (e) {}
    }
    return response;
  };

  const originalOpen = XMLHttpRequest.prototype.open;
  const originalSend = XMLHttpRequest.prototype.send;

  XMLHttpRequest.prototype.open = function(method, url, ...rest) {
    this._url = url;
    return originalOpen.apply(this, [method, url, ...rest]);
  };

  XMLHttpRequest.prototype.send = function(...args) {
    this._cinemanaDlPageUrl = location.href;
    this.addEventListener('load', function() {
      const url = this._url;
      if (url && typeof url === 'string') {
        if (isSubtitleUrl(url)) {
          postApiData(url, { url: url }, this._cinemanaDlPageUrl);
        }

        if (!isJsonXhr(this)) {
          return;
        }

        try {
          const data = JSON.parse(this.responseText);
          postApiData(url, data, this._cinemanaDlPageUrl);
        } catch (e) {}
      }
    });
    return originalSend.apply(this, args);
  };
})();
