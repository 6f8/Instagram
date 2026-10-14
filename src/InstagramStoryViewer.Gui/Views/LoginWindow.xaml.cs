using System.Windows;
using System.Windows.Threading;
using InstagramStoryViewer.Gui.Services;
using Microsoft.Web.WebView2.Core;

namespace InstagramStoryViewer.Gui.Views;

/// <summary>
/// Shows instagram.com in an embedded Edge (WebView2) browser and captures the session cookie once
/// the user has logged in. The password never passes through this app.
/// </summary>
public partial class LoginWindow : Window
{
    private const string LoginUrl = "https://www.instagram.com/accounts/login/";

    private readonly bool _clearExistingSession;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _done;
    private bool _checking;
    private bool _closed;

    public string? CookieHeader { get; private set; }

    public LoginWindow(bool clearExistingSession)
    {
        InitializeComponent();
        _clearExistingSession = clearExistingSession;
        _poll.Tick += async (_, _) => await CheckCookiesAsync();
        Loaded += async (_, _) => await InitializeBrowserAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _poll.Stop();
            Web.Dispose();
        };
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.WebViewDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, SettingsStore.WebViewDataFolder);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception) when (_closed)
        {
            return; // the user closed the window while the browser was starting: nothing to report
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ConfirmDialog.Info(this,
                "مكوّن Microsoft Edge WebView2 غير مثبت على جهازك.\n" +
                "ثبّته من موقع مايكروسوفت (Evergreen Runtime)، أو استخدم طريقة لصق الكوكيز يدوياً.");
            Close();
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException
                                       or UnauthorizedAccessException or IOException)
        {
            ConfirmDialog.Info(this, $"تعذّر تشغيل المتصفح المدمج: {ex.Message}");
            Close();
            return;
        }
        if (_closed) return;

        var core = Web.CoreWebView2;
        if (_clearExistingSession)
            core.CookieManager.DeleteAllCookies();

        core.Settings.AreDevToolsEnabled = false;
        core.NavigationStarting += (_, _) => Busy.Visibility = Visibility.Visible;
        core.NavigationCompleted += async (_, _) =>
        {
            Busy.Visibility = Visibility.Collapsed;
            await CheckCookiesAsync();
        };
        // Links that would open a popup (e.g. "Log in with Facebook") stay in this window.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };

        _poll.Start();
        core.Navigate(LoginUrl);
    }

    private async Task CheckCookiesAsync()
    {
        if (_done || _checking || _closed || Web.CoreWebView2 is null) return;
        _checking = true;
        try
        {
            var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.instagram.com");
            if (!cookies.Any(c => c.Name == "sessionid" && !string.IsNullOrEmpty(c.Value))) return;

            _done = true;
            _poll.Stop();
            CookieHeader = string.Join("; ", cookies
                .Where(c => !string.IsNullOrEmpty(c.Value))
                .GroupBy(c => c.Name)
                .Select(g => $"{g.Key}={g.First().Value}"));
            StatusText.Text = "تم تسجيل الدخول ✔";
            DialogResult = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The browser is shutting down or not ready yet; the next tick will retry.
        }
        finally
        {
            _checking = false;
        }
    }
}
