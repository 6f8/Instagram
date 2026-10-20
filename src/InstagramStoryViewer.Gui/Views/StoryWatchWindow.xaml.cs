using System.Text.Json;
using System.Windows;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;
using Microsoft.Web.WebView2.Core;

namespace InstagramStoryViewer.Gui.Views;

/// <summary>
/// Small corner window that plays stories in the embedded browser so Instagram records a genuine view.
/// Instagram's web app shows a "View story" confirmation before playing a story opened by URL; this window
/// presses it automatically. One instance is reused for every batch (manual and automatic).
/// </summary>
public partial class StoryWatchWindow : Window
{
    /// <summary>How long each story stays on screen after it starts playing.</summary>
    private static readonly TimeSpan ViewDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    // The page scripts below receive the selectors (PageSelectors) as their first argument, so the
    // labels and element queries can be updated without changing code.

    // Finds the "View story" button and clicks it.
    private const string ClickViewStoryScript = """
        ((cfg) => {
          if (location.pathname.startsWith('/accounts/login')) return 'login';
          const re = new RegExp(cfg.viewStory, 'i');
          const btn = [...document.querySelectorAll('[role="button"], button')]
            .find(e => re.test((e.innerText || '').trim()));
          if (btn) { btn.click(); return 'clicked'; }
          if (location.pathname.includes('/stories/') && document.querySelector('section video, section img[draggable="false"], video')) return 'playing';
          return 'waiting';
        })(__CFG__)
        """;

    // Likes the post/story on the page. Comment hearts carry the same label, so the largest heart icon is
    // taken (the post's own like button is 24 px, comment hearts are 12 px).
    // mode 'check' only reports the state; 'click' clicks when not yet liked.
    private const string LikeScript = """
        ((cfg, mode) => {
          if (location.pathname.startsWith('/accounts/login')) return 'login';
          const liked = new RegExp(cfg.liked, 'i');
          const like = new RegExp(cfg.like, 'i');
          const hearts = [...document.querySelectorAll('svg[aria-label]')]
            .filter(s => { const l = s.getAttribute('aria-label').trim(); return liked.test(l) || like.test(l); })
            .map(s => ({ s, w: s.getBoundingClientRect().width }))
            .filter(h => h.w > 0)
            .sort((a, b) => b.w - a.w);
          if (!hearts.length) return 'missing';
          const top = hearts[0].s;
          if (liked.test(top.getAttribute('aria-label').trim())) return 'liked';
          if (mode !== 'click') return 'unliked';
          const btn = top.closest('button, [role="button"]') || top.parentElement;
          btn.click();
          return 'clicked';
        })(__CFG__, __MODE__)
        """;

    // The story data the page embedded in its HTML (server-rendered JSON), when it is there.
    private const string EmbeddedStoryDataScript = """
        (() => [...document.querySelectorAll('script[type="application/json"]')]
          .map(s => s.textContent || '')
          .filter(t => t.includes('taken_at') && (t.includes('reels_media') || t.includes('image_versions2'))))()
        """;

    private readonly PageSelectors _selectors;

    /// <summary>
    /// Fills a script's __NAME__ placeholders with JSON values in a single pass, so text inserted for one
    /// placeholder is never scanned for another.
    /// </summary>
    private string Script(string template, params (string Name, object? Value)[] args)
    {
        var values = args.ToDictionary(a => a.Name, a => JsonSerializer.Serialize(a.Value));
        values["CFG"] = _selectors.ToScriptArgument();
        return System.Text.RegularExpressions.Regex.Replace(template, "__([A-Z]+)__",
            m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    /// <summary>
    /// Runs a page script. Waiting stops when the batch is stopped or the window closes (a closed WebView2 may
    /// never complete a pending call, which would otherwise hold the batch, and the queue behind it, forever).
    /// </summary>
    private async Task<T?> RunAsync<T>(CoreWebView2 core, string template, params (string Name, object? Value)[] args) =>
        JsonSerializer.Deserialize<T>(await core.ExecuteScriptAsync(Script(template, args)).WaitAsync(_batchCts.Token));

    /// <summary>Unsubscribes, tolerating a WebView2 that was closed in the meantime.</summary>
    private static void Unsubscribe(CoreWebView2 core, EventHandler<CoreWebView2NavigationCompletedEventArgs> handler)
    {
        try { core.NavigationCompleted -= handler; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private CancellationTokenSource _batchCts = new();
    private Task? _init;
    private bool _closed;

    public StoryWatchWindow(PageSelectors selectors)
    {
        _selectors = selectors;
        InitializeComponent();
        Closed += (_, _) =>
        {
            _closed = true;
            _batchCts.Cancel();
            Web.Dispose();
        };
    }

    public bool IsAlive => !_closed;

    /// <summary>Places the window in the bottom-left corner of the owner's screen work area.</summary>
    public void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + 16;
        Top = area.Bottom - Height - 16;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _batchCts.Cancel();

    /// <summary>Stops the batch that is running (same as the window's Stop button).</summary>
    public void StopCurrent() => _batchCts.Cancel();

    /// <summary>Plays the given stories in order. Returns, per URL, whether it was shown.</summary>
    /// <param name="onShown">Called after each story is shown, with the number shown so far (live progress).</param>
    /// <param name="like">Also press the story's like button once it has played (onLiked reports each result).</param>
    public async Task<IReadOnlyList<bool>> PlayAsync(string cookieHeader, IReadOnlyList<string> urls, Action<int>? onShown = null,
        bool like = false, Action<int, LikeOutcome>? onLiked = null)
    {
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;
        var shown = new bool[urls.Count];

        try
        {
            _init ??= InitializeBrowserAsync();
            await _init;
            if (_closed) return shown;

            var core = Web.CoreWebView2;
            ApplySessionCookies(core, cookieHeader);

            for (var i = 0; i < urls.Count; i++)
            {
                StatusText.Text = urls.Count == 1 ? "جاري مشاهدة الستوري..." : $"مشاهدة {i + 1} من {urls.Count}...";
                Progress.Value = (double)i / urls.Count;

                var result = await PlayOneAsync(core, urls[i], ct);
                if (result == "waiting") AppLog.Warn("Watch: the story never started playing (selectors may need an update)");
                if (result == "login")
                {
                    StatusText.Text = "المتصفح غير مسجّل الدخول";
                    break;
                }
                if (result is not ("clicked" or "playing")) continue; // story unavailable (expired/deleted)

                // Vary the dwell time slightly, like a person would.
                await Task.Delay(ViewDuration + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1500)), ct);
                shown[i] = true;
                onShown?.Invoke(shown.Count(x => x));
                if (like)
                {
                    var liked = await ClickLikeAsync(core, ct);
                    onLiked?.Invoke(i, liked);
                    await Task.Delay(TimeSpan.FromMilliseconds(500 + Random.Shared.Next(0, 800)), ct);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            // stopped by the user, or the window was closed mid-call
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or UnauthorizedAccessException or IOException)
        {
            if (!_closed) // no dialog owned by a window that is already gone
            {
                ConfirmDialog.Info(this, ex is WebView2RuntimeNotFoundException
                    ? "مكوّن Microsoft Edge WebView2 غير مثبت. ثبّته من موقع مايكروسوفت (Evergreen Runtime)."
                    : $"تعذّر تشغيل المتصفح المدمج: {ex.Message}");
                Close();
            }
        }

        if (!_closed)
        {
            Progress.Value = 1;
            StatusText.Text = $"تمت مشاهدة {shown.Count(x => x)}";
            // Park on a blank page so nothing keeps playing between batches.
            try { Web.CoreWebView2?.Navigate("about:blank"); }
            catch (InvalidOperationException) { }
        }
        return shown;
    }

    /// <summary>
    /// Opens each story/post and presses its like button, then checks the button flipped to "Unlike".
    /// Stories are played first (the "View story" step), so liking a story also counts as viewing it.
    /// </summary>
    public async Task<IReadOnlyList<LikeOutcome>> LikeAsync(string cookieHeader, IReadOnlyList<LikeRequest> requests)
    {
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;
        var results = Enumerable.Repeat(LikeOutcome.NotShown, requests.Count).ToArray();

        try
        {
            _init ??= InitializeBrowserAsync();
            await _init;
            if (_closed) return results;

            var core = Web.CoreWebView2;
            ApplySessionCookies(core, cookieHeader);

            for (var i = 0; i < requests.Count; i++)
            {
                var r = requests[i];
                StatusText.Text = $"إعجاب {i + 1} من {requests.Count}...";
                Progress.Value = (double)i / requests.Count;

                var opened = r.IsStory ? await PlayOneAsync(core, r.Url, ct) : await OpenPageAsync(core, r.Url, ct);
                if (opened == "login")
                {
                    StatusText.Text = "المتصفح غير مسجّل الدخول";
                    break;
                }
                if (opened is not ("clicked" or "playing" or "loaded")) continue;

                // A short, varying pause before reacting, then wait for the like button to render.
                await Task.Delay(TimeSpan.FromMilliseconds(1200 + Random.Shared.Next(0, 1500)), ct);
                results[i] = await ClickLikeAsync(core, ct);
                if (results[i] == LikeOutcome.NoButton) AppLog.Warn("Like: no like button found on the page (selectors may need an update)");
                await Task.Delay(TimeSpan.FromMilliseconds(800 + Random.Shared.Next(0, 1200)), ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            // stopped by the user, or the window was closed mid-call
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or UnauthorizedAccessException or IOException)
        {
            if (!_closed) // no dialog owned by a window that is already gone
            {
                ConfirmDialog.Info(this, ex is WebView2RuntimeNotFoundException
                    ? "مكوّن Microsoft Edge WebView2 غير مثبت. ثبّته من موقع مايكروسوفت (Evergreen Runtime)."
                    : $"تعذّر تشغيل المتصفح المدمج: {ex.Message}");
                Close();
            }
        }

        if (!_closed)
        {
            Progress.Value = 1;
            StatusText.Text = $"إعجابات: {results.Count(x => x == LikeOutcome.Liked)}";
            try { Web.CoreWebView2?.Navigate("about:blank"); }
            catch (InvalidOperationException) { }
        }
        return results;
    }

    /// <summary>
    /// Opens the account's story page without pressing "View story" (so nothing is marked as seen) and returns
    /// the JSON the page loaded: what it embedded in its HTML and the answers of its own data requests. Used
    /// when Instagram refuses the stories endpoint for an account whose stories the page still shows.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadStoryPageAsync(string cookieHeader, string username)
    {
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;
        var captured = new List<string>();
        CoreWebView2? core = null;

        async void OnResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                var uri = e.Request.Uri;
                if (!uri.Contains("/graphql", StringComparison.Ordinal) && !uri.Contains("reels_media", StringComparison.Ordinal)) return;
                using var stream = await e.Response.GetContentAsync();
                if (stream is null) return;
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync(ct);
                if (text.Contains("taken_at", StringComparison.Ordinal)) lock (captured) captured.Add(text);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                           or ObjectDisposedException or IOException or OperationCanceledException)
            {
                // the body was not available (redirect, aborted request) or the window closed
            }
        }

        try
        {
            _init ??= InitializeBrowserAsync();
            await _init;
            if (_closed) return captured;

            core = Web.CoreWebView2;
            ApplySessionCookies(core, cookieHeader);
            StatusText.Text = "جاري فتح صفحة الستوري...";
            Progress.IsIndeterminate = true;
            core.WebResourceResponseReceived += OnResponse;

            var opened = await OpenPageAsync(core, $"https://www.instagram.com/stories/{Uri.EscapeDataString(username)}/", ct);
            if (opened == "login") return captured;

            // The data arrives with the page or a moment later; wait for it, at most a few seconds.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                if (await RunAsync<string[]>(core, EmbeddedStoryDataScript) is { Length: > 0 } embedded)
                    lock (captured) captured.AddRange(embedded);
                lock (captured) if (captured.Count > 0) break;
                await Task.Delay(800, ct);
            }
            // Answers still arriving get a short moment more.
            await Task.Delay(1000, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or WebView2RuntimeNotFoundException
                                       or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn($"Story page read for {username} stopped: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            if (core is not null)
            {
                try { core.WebResourceResponseReceived -= OnResponse; }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            }
            if (!_closed)
            {
                Progress.IsIndeterminate = false;
                StatusText.Text = "انتهى";
                try { Web.CoreWebView2?.Navigate("about:blank"); }
                catch (InvalidOperationException) { }
            }
        }
        lock (captured) return captured.ToList();
    }

    /// <summary>Waits for the like button, clicks it if needed, and verifies the result.</summary>
    private async Task<LikeOutcome> ClickLikeAsync(CoreWebView2 core, CancellationToken ct)
    {
        // The button can take a moment to render.
        var state = "missing";
        for (var attempt = 0; attempt < 8 && state == "missing"; attempt++)
        {
            state = await RunAsync<string>(core, LikeScript, ("MODE", "check")) ?? "missing";
            if (state == "missing") await Task.Delay(600, ct);
        }
        if (state == "missing") return LikeOutcome.NoButton;
        if (state == "liked") return LikeOutcome.AlreadyLiked;
        if (state == "login") return LikeOutcome.NotShown;

        await RunAsync<string>(core, LikeScript, ("MODE", "click"));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(600, ct);
            if (await RunAsync<string>(core, LikeScript, ("MODE", "check")) == "liked") return LikeOutcome.Liked;
        }
        return LikeOutcome.Failed;
    }

    /// <summary>Opens a regular page (a post). Returns "loaded", "login" or "failed".</summary>
    private static async Task<string> OpenPageAsync(CoreWebView2 core, string url, CancellationToken ct)
    {
        var navigated = new TaskCompletionSource<bool>();
        void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => navigated.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += OnCompleted;
        try
        {
            core.Navigate(url);
            var ok = await navigated.Task.WaitAsync(StartTimeout, ct);
            if (!ok) return "failed";
        }
        catch (TimeoutException)
        {
            // continue; the page may still have rendered
        }
        finally
        {
            Unsubscribe(core, OnCompleted);
        }
        return core.Source.Contains("/accounts/login", StringComparison.OrdinalIgnoreCase) ? "login" : "loaded";
    }

    private async Task InitializeBrowserAsync()
    {
        Directory.CreateDirectory(SettingsStore.WebViewDataFolder);
        var env = await CoreWebView2Environment.CreateAsync(null, SettingsStore.WebViewDataFolder);
        await Web.EnsureCoreWebView2Async(env);
        Web.CoreWebView2.IsMuted = true;
        Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
    }

    /// <summary>Navigates to a story and waits until it is playing. Returns the script's last state.</summary>
    private async Task<string> PlayOneAsync(CoreWebView2 core, string url, CancellationToken ct)
    {
        var navigated = new TaskCompletionSource();
        void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => navigated.TrySetResult();
        core.NavigationCompleted += OnCompleted;
        try
        {
            core.Navigate(url);
            await navigated.Task.WaitAsync(StartTimeout, ct);
        }
        catch (TimeoutException)
        {
            // keep going; the page may still be usable
        }
        finally
        {
            Unsubscribe(core, OnCompleted);
        }

        var state = "waiting";
        var deadline = DateTime.UtcNow + StartTimeout;
        var clicked = false;
        var polls = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            state = await RunAsync<string>(core, ClickViewStoryScript) ?? "waiting";
            polls++;
            if (state == "login") return state;
            // Only trust "playing" without a click after a few polls: the confirmation screen shows a
            // blurred preview image before its button renders.
            if (state == "playing" && (clicked || polls >= 4)) return state;
            if (state == "clicked") clicked = true;
            else if (clicked) return "clicked"; // button gone after clicking: the story is playing
            await Task.Delay(700, ct);
        }
        return clicked ? "clicked" : state;
    }

    /// <summary>Puts the app's current session into the browser, so pasting a cookie also works here.</summary>
    private static void ApplySessionCookies(CoreWebView2 core, string cookieHeader)
    {
        foreach (var part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var name = part[..eq].Trim();
            if (name is not ("sessionid" or "csrftoken" or "ds_user_id" or "mid" or "ig_did" or "rur")) continue;
            var cookie = core.CookieManager.CreateCookie(name, part[(eq + 1)..].Trim(), ".instagram.com", "/");
            cookie.IsSecure = true;
            cookie.IsHttpOnly = name == "sessionid";
            core.CookieManager.AddOrUpdateCookie(cookie);
        }
    }
}
