using System.Text.Json;
using System.Windows;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Social;
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

    // Shared by the comment scripts: the visible, enabled comment box of the post.
    private const string FindCommentBox = """
        const box = [...document.querySelectorAll(cfg.commentBox)]
          .find(t => t.getBoundingClientRect().width > 0 && !t.disabled && t.getAttribute('aria-disabled') !== 'true');
        """;

    // Finds the comment box and focuses it so the text can be typed into it.
    private const string FocusCommentBoxScript = """
        ((cfg) => {
          if (location.pathname.startsWith('/accounts/login')) return 'login';
          if (new RegExp(cfg.commentsOff, 'i').test(document.body.innerText || '')) return 'off';
          __BOX__
          if (!box) {
            // Narrow (mobile) layout: the box only appears after pressing the post's comment icon.
            const icon = new RegExp(cfg.commentIcon, 'i');
            const svg = [...document.querySelectorAll('svg[aria-label]')]
              .find(s => icon.test(s.getAttribute('aria-label').trim()) && s.getBoundingClientRect().width > 0);
            const btn = svg && (svg.closest('button, [role="button"], a') || svg.parentElement);
            if (btn && !window.__igsvOpenedComments) { window.__igsvOpenedComments = true; btn.click(); return 'opening'; }
            return 'missing';
          }
          box.scrollIntoView({ block: 'center' });
          box.focus();
          box.click();
          return 'ready';
        })(__CFG__)
        """;

    // Returns what the comment box currently holds (null when there is none).
    private const string CommentBoxValueScript = """
        ((cfg) => { __BOX__ return box ? box.value : null; })(__CFG__)
        """;

    // Fallback when typing did not reach the box: set the value the way React expects and announce it.
    private const string SetCommentValueScript = """
        ((cfg, text) => {
          __BOX__
          if (!box) return false;
          const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set;
          setter.call(box, text);
          box.dispatchEvent(new Event('input', { bubbles: true }));
          return box.value === text;
        })(__CFG__, __TEXT__)
        """;

    // Presses the "Post" button next to the comment box.
    private const string SubmitCommentScript = """
        ((cfg) => {
          __BOX__
          if (!box) return 'missing';
          const re = new RegExp(cfg.postButton, 'i');
          const scope = box.closest('form') || box.parentElement?.parentElement?.parentElement || document;
          const btn = [...scope.querySelectorAll('[role="button"], button, div[tabindex]')]
            .find(b => re.test((b.innerText || '').trim()));
          if (!btn) return 'nobutton';
          if (btn.disabled || btn.getAttribute('aria-disabled') === 'true') return 'disabled';
          btn.click();
          return 'clicked';
        })(__CFG__)
        """;

    // After posting: an error message, the comment shown on the page, or still waiting.
    private const string CheckCommentScript = """
        ((cfg, snippet) => {
          const body = document.body.innerText || '';
          if (new RegExp(cfg.commentError, 'i').test(body)) return 'error';
          __BOX__
          const cleared = !box || box.value.trim() === '';
          if (cleared && body.includes(snippet)) return 'posted';
          return cleared ? 'cleared' : 'pending';
        })(__CFG__, __SNIPPET__)
        """;

    // A same-origin request from inside instagram.com, with the headers the site's own code sends.
    private const string PageFetchScript = """
        (async (path) => {
          const csrf = (document.cookie.match(/(?:^|; )csrftoken=([^;]+)/) || [])[1] || '';
          const claim = sessionStorage.getItem('www-claim-v2') || '0';
          const r = await fetch(path, {
            credentials: 'include',
            headers: { 'X-IG-App-ID': '936619743392459', 'X-Requested-With': 'XMLHttpRequest',
                       'X-CSRFToken': csrf, 'X-IG-WWW-Claim': claim, 'X-ASBD-ID': '129477' }
          });
          return JSON.stringify({ status: r.status, body: await r.text() });
        })(__PATH__)
        """;

    private readonly PageSelectors _selectors;

    /// <summary>
    /// Fills a script's __NAME__ placeholders with JSON values in a single pass, so text inserted for one
    /// placeholder (a comment, say) is never scanned for another.
    /// </summary>
    private string Script(string template, params (string Name, object? Value)[] args)
    {
        var values = args.ToDictionary(a => a.Name, a => JsonSerializer.Serialize(a.Value));
        values["CFG"] = _selectors.ToScriptArgument();
        return System.Text.RegularExpressions.Regex.Replace(template.Replace("__BOX__", FindCommentBox), "__([A-Z]+)__",
            m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    private async Task<T?> RunAsync<T>(CoreWebView2 core, string template, params (string Name, object? Value)[] args) =>
        JsonSerializer.Deserialize<T>(await core.ExecuteScriptAsync(Script(template, args)));

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

    private Rect? _cornerBounds;

    /// <summary>
    /// Comments need Instagram's desktop layout (comment box under the post); the 300 px corner window gets the
    /// mobile one. The window is widened and centred while commenting, then put back in its corner.
    /// </summary>
    private void UseWideLayout(bool wide)
    {
        if (wide)
        {
            _cornerBounds ??= new Rect(Left, Top, Width, Height);
            var area = SystemParameters.WorkArea;
            Width = Math.Min(1000, area.Width - 40);
            Height = Math.Min(760, area.Height - 40);
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }
        else if (_cornerBounds is { } r)
        {
            Width = r.Width;
            Height = r.Height;
            Left = r.Left;
            Top = r.Top;
            _cornerBounds = null;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _batchCts.Cancel();

    /// <summary>Stops the batch that is running (same as the window's Stop button).</summary>
    public void StopCurrent() => _batchCts.Cancel();

    /// <summary>Plays the given stories in order. Returns, per URL, whether it was shown.</summary>
    /// <param name="onShown">Called after each story is shown, with the number shown so far (live progress).</param>
    public async Task<IReadOnlyList<bool>> PlayAsync(string cookieHeader, IReadOnlyList<string> urls, Action<int>? onShown = null)
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

    /// <summary>
    /// Opens instagram.com in this window and hands <paramref name="work"/> a function that fetches API paths
    /// from inside the page — the same requests, cookies and headers as the site itself (Instagram refuses
    /// some of them, such as follow lists, from outside the browser). The Stop button cancels the work.
    /// </summary>
    public async Task<T> UseInstagramPageAsync<T>(string cookieHeader,
        Func<Func<string, CancellationToken, Task<PageResult>>, CancellationToken, Task<T>> work)
    {
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;
        try
        {
            _init ??= InitializeBrowserAsync();
            await _init;
            if (_closed) throw new OperationCanceledException();

            var core = Web.CoreWebView2;
            ApplySessionCookies(core, cookieHeader);
            StatusText.Text = "جاري فتح إنستجرام...";
            Progress.IsIndeterminate = true;
            var opened = await OpenPageAsync(core, "https://www.instagram.com/", ct);
            if (opened == "login")
                throw new InstagramApiException(InstagramErrorKind.LoginRequired, "المتصفح المدمج غير مسجّل الدخول.");
            if (opened != "loaded")
                throw new InstagramApiException(InstagramErrorKind.Network, "تعذّر فتح إنستجرام في المتصفح المدمج.");
            StatusText.Text = "جاري القراءة من إنستجرام...";

            async Task<PageResult> FetchAsync(string path, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                ct.ThrowIfCancellationRequested();
                // Runtime.evaluate can wait for the promise; ExecuteScriptAsync cannot.
                var args = JsonSerializer.Serialize(new
                {
                    expression = PageFetchScript.Replace("__PATH__", JsonSerializer.Serialize(path)),
                    awaitPromise = true,
                    returnByValue = true
                });
                using var reply = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args));
                if (reply.RootElement.TryGetProperty("exceptionDetails", out var error))
                    throw new InstagramApiException(InstagramErrorKind.Network, $"تعذّر الطلب داخل الصفحة: {error.GetRawText()}");
                using var page = JsonDocument.Parse(reply.RootElement.GetProperty("result").GetProperty("value").GetString()!);
                return new PageResult(page.RootElement.GetProperty("status").GetInt32(), page.RootElement.GetProperty("body").GetString() ?? "");
            }

            return await work(FetchAsync, ct);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw new InstagramApiException(InstagramErrorKind.Unknown,
                "مكوّن Microsoft Edge WebView2 غير مثبت. ثبّته من موقع مايكروسوفت (Evergreen Runtime).");
        }
        finally
        {
            if (!_closed)
            {
                Progress.IsIndeterminate = false;
                StatusText.Text = "انتهى";
                try { Web.CoreWebView2?.Navigate("about:blank"); }
                catch (InvalidOperationException) { }
            }
        }
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

    /// <summary>
    /// Opens a post and posts the user's comment: focuses the comment box, types the text (as keyboard input,
    /// so the page's own handlers see it), presses "Post", then waits until the comment shows on the page.
    /// </summary>
    public async Task<CommentOutcome> CommentAsync(string cookieHeader, string postUrl, string text)
    {
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;
        var outcome = CommentOutcome.NotShown;
        try
        {
            _init ??= InitializeBrowserAsync();
            await _init;
            if (_closed) return outcome;

            var core = Web.CoreWebView2;
            ApplySessionCookies(core, cookieHeader);
            UseWideLayout(true);
            StatusText.Text = "جاري فتح المنشور...";
            Progress.Value = 0.1;

            var opened = await OpenPageAsync(core, postUrl, ct);
            if (opened == "login") return outcome = CommentOutcome.LoginRequired;
            if (opened != "loaded") return outcome;

            outcome = await PostCommentAsync(core, text, ct);
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
            UseWideLayout(false);
            Progress.Value = 1;
            StatusText.Text = outcome == CommentOutcome.Posted ? "تم نشر التعليق" : "انتهى";
            try { Web.CoreWebView2?.Navigate("about:blank"); }
            catch (InvalidOperationException) { }
        }
        return outcome;
    }

    private async Task<CommentOutcome> PostCommentAsync(CoreWebView2 core, string text, CancellationToken ct)
    {
        StatusText.Text = "جاري كتابة التعليق...";
        Progress.Value = 0.35;

        // The comment box renders a moment after the page.
        var state = "missing";
        for (var attempt = 0; attempt < 14 && state is "missing" or "opening"; attempt++)
        {
            await Task.Delay(attempt == 0 ? 1500 : 700, ct);
            state = await RunAsync<string>(core, FocusCommentBoxScript) ?? "missing";
        }
        switch (state)
        {
            case "login": return CommentOutcome.LoginRequired;
            case "off": return CommentOutcome.CommentsOff;
            case "missing":
                AppLog.Warn("Comment: no comment box on the page (comments off, or selectors need an update)");
                return CommentOutcome.CommentsOff;
        }

        // Type it like a keyboard would; fall back to setting the value if the page did not take it.
        await Task.Delay(400 + Random.Shared.Next(0, 500), ct);
        await core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text }));
        await Task.Delay(500, ct);
        if (await RunAsync<string>(core, CommentBoxValueScript) != text && !await RunAsync<bool>(core, SetCommentValueScript, ("TEXT", text)))
        {
            AppLog.Warn("Comment: the text did not reach the comment box");
            return CommentOutcome.Rejected;
        }

        StatusText.Text = "جاري النشر...";
        Progress.Value = 0.65;
        await Task.Delay(700 + Random.Shared.Next(0, 700), ct);
        var submit = await RunAsync<string>(core, SubmitCommentScript);
        if (submit is "nobutton" or "disabled")
        {
            // Enter also posts a comment on Instagram's web page (Shift+Enter is a new line).
            AppLog.Info($"Comment: post button {submit}, pressing Enter instead");
            foreach (var type in new[] { "keyDown", "keyUp" })
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
                {
                    type, key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13, text = type == "keyDown" ? "\r" : null
                }));
        }

        // The comment counts once the box is empty and the text shows on the page.
        var snippet = text.Split('\n')[0].Trim();
        if (snippet.Length > 40) snippet = snippet[..40];
        var last = "pending";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(600, ct);
            last = await RunAsync<string>(core, CheckCommentScript, ("SNIPPET", snippet)) ?? "pending";
            if (last == "posted") return CommentOutcome.Posted;
            if (last == "error") return CommentOutcome.Rejected;
        }
        return last == "cleared" ? CommentOutcome.Unconfirmed : CommentOutcome.Rejected;
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
            core.NavigationCompleted -= OnCompleted;
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
