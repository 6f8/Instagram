using System.Text.Json;
using System.Windows;
using InstagramStoryViewer.Gui.Services;
using Microsoft.Web.WebView2.Core;

namespace InstagramStoryViewer.Gui.Views;

/// <summary>
/// Plays stories in the embedded browser so Instagram records a genuine view. Instagram's web app shows a
/// "View story" confirmation before playing a story opened by URL; this window presses it automatically.
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

    private readonly string _cookieHeader;
    private readonly IReadOnlyList<string> _urls;
    private readonly CancellationTokenSource _cts = new();

    public int WatchedCount { get; private set; }

    public StoryWatchWindow(string cookieHeader, IReadOnlyList<string> storyUrls)
    {
        InitializeComponent();
        _cookieHeader = cookieHeader;
        _urls = storyUrls;
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) =>
        {
            _cts.Cancel();
            Web.Dispose();
        };
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RunAsync()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.WebViewDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, SettingsStore.WebViewDataFolder);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException)
        {
            ConfirmDialog.Info(this, ex is WebView2RuntimeNotFoundException
                ? "مكوّن Microsoft Edge WebView2 غير مثبت. ثبّته من موقع مايكروسوفت (Evergreen Runtime)."
                : $"تعذّر تشغيل المتصفح المدمج: {ex.Message}");
            Close();
            return;
        }

        var core = Web.CoreWebView2;
        core.IsMuted = true;
        core.Settings.AreDevToolsEnabled = false;
        ApplySessionCookies(core);

        try
        {
            for (var i = 0; i < _urls.Count; i++)
            {
                StatusText.Text = _urls.Count == 1 ? "جاري مشاهدة الستوري..." : $"جاري مشاهدة الستوري {i + 1} من {_urls.Count}...";
                Progress.Value = (double)i / _urls.Count;

                var result = await PlayAsync(core, _urls[i], _cts.Token);
                if (result == "login")
                {
                    ConfirmDialog.Info(this, "المتصفح المدمج غير مسجّل الدخول في إنستجرام. سجّل الخروج من البرنامج ثم ادخل عبر زر \"تسجيل الدخول عبر إنستجرام\".");
                    break;
                }
                if (result is not ("clicked" or "playing")) continue; // story unavailable (expired/deleted)

                await Task.Delay(ViewDuration, _cts.Token);
                WatchedCount = i + 1;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            // stopped by the user (closing the window disposes the browser mid-call)
        }

        if (IsLoaded) Close();
    }

    /// <summary>Navigates to a story and waits until it is playing. Returns the script's last state.</summary>
    private static async Task<string> PlayAsync(CoreWebView2 core, string url, CancellationToken ct)
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

    /// <summary>Puts the app's session into the browser, so pasting a cookie also works here.</summary>
    private void ApplySessionCookies(CoreWebView2 core)
    {
        foreach (var part in _cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
