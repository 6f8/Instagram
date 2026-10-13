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
