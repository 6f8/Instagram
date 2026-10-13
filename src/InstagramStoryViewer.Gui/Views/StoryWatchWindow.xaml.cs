using System.Text.Json;
using System.Windows;
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

    // Finds the "View story" button in any of the common UI languages and clicks it.
    private const string ClickViewStoryScript = """
        (() => {
          if (location.pathname.startsWith('/accounts/login')) return 'login';
          const re = /^(view story|watch story|عرض القصة|مشاهدة القصة|عرض الستوري|ver historia|voir la story|story ansehen|hikayeyi gör)/i;
          const btn = [...document.querySelectorAll('[role="button"], button')]
            .find(e => re.test((e.innerText || '').trim()));
          if (btn) { btn.click(); return 'clicked'; }
          if (location.pathname.includes('/stories/') && document.querySelector('section video, section img[draggable="false"], video')) return 'playing';
          return 'waiting';
        })()
        """;

    // Likes the post/story on the page. Comment hearts carry the same label, so the largest heart icon is
    // taken (the post's own like button is 24 px, comment hearts are 12 px). Labels cover common UI languages.
    // mode 'check' only reports the state; 'click' clicks when not yet liked.
    private const string LikeScriptTemplate = """
        ((mode) => {
          if (location.pathname.startsWith('/accounts/login')) return 'login';
          const liked = /^(unlike|إلغاء الإعجاب|ya no me gusta|je n.aime plus|gefällt mir nicht mehr|beğenmekten vazgeç)$/i;
          const like = /^(like|أعجبني|me gusta|j.aime|gefällt mir|beğen)$/i;
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
        })('{MODE}')
        """;

    private CancellationTokenSource _batchCts = new();
    private Task? _init;
    private bool _closed;

    public StoryWatchWindow()
    {
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

    /// <summary>Plays the given stories in order. Returns, per URL, whether it was shown.</summary>
    public async Task<IReadOnlyList<bool>> PlayAsync(string cookieHeader, IReadOnlyList<string> urls)
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
                if (result == "login")
                {
                    StatusText.Text = "المتصفح غير مسجّل الدخول";
                    break;
                }
                if (result is not ("clicked" or "playing")) continue; // story unavailable (expired/deleted)

                // Vary the dwell time slightly, like a person would.
                await Task.Delay(ViewDuration + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1500)), ct);
                shown[i] = true;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            // stopped by the user, or the window was closed mid-call
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or UnauthorizedAccessException or IOException)
        {
            ConfirmDialog.Info(this, ex is WebView2RuntimeNotFoundException
                ? "مكوّن Microsoft Edge WebView2 غير مثبت. ثبّته من موقع مايكروسوفت (Evergreen Runtime)."
                : $"تعذّر تشغيل المتصفح المدمج: {ex.Message}");
            Close();
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
            ConfirmDialog.Info(this, ex is WebView2RuntimeNotFoundException
                ? "مكوّن Microsoft Edge WebView2 غير مثبت. ثبّته من موقع مايكروسوفت (Evergreen Runtime)."
                : $"تعذّر تشغيل المتصفح المدمج: {ex.Message}");
            Close();
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

    /// <summary>Waits for the like button, clicks it if needed, and verifies the result.</summary>
    private static async Task<LikeOutcome> ClickLikeAsync(CoreWebView2 core, CancellationToken ct)
    {
        string Script(string mode) => LikeScriptTemplate.Replace("{MODE}", mode);

        // The button can take a moment to render.
        var state = "missing";
        for (var attempt = 0; attempt < 8 && state == "missing"; attempt++)
        {
            state = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(Script("check"))) ?? "missing";
            if (state == "missing") await Task.Delay(600, ct);
        }
        if (state == "missing") return LikeOutcome.NoButton;
        if (state == "liked") return LikeOutcome.AlreadyLiked;
        if (state == "login") return LikeOutcome.NotShown;

        await core.ExecuteScriptAsync(Script("click"));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(600, ct);
            var after = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(Script("check")));
            if (after == "liked") return LikeOutcome.Liked;
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
            core.NavigationCompleted -= OnCompleted;
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
    private static async Task<string> PlayOneAsync(CoreWebView2 core, string url, CancellationToken ct)
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
            core.NavigationCompleted -= OnCompleted;
        }

        var state = "waiting";
        var deadline = DateTime.UtcNow + StartTimeout;
        var clicked = false;
        var polls = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var json = await core.ExecuteScriptAsync(ClickViewStoryScript);
            state = JsonSerializer.Deserialize<string>(json) ?? "waiting";
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
