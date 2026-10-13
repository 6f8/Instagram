using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;
using InstagramStoryViewer.Services;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>Login, logout, session persistence and the stories tray refresh.</summary>
public sealed partial class MainViewModel
{
    public async Task InitializeAsync()
    {
        CleanVideoCache();
        RefreshDownloads();
        var saved = SettingsStore.Unprotect(_settings.ProtectedSession);
        if (saved is not null)
            await TryLoginAsync(saved, persist: true);
    }

    [RelayCommand]
    private async Task LoginWithCookieAsync(string? cookieText)
    {
        if (string.IsNullOrWhiteSpace(cookieText))
        {
            LoginError = "الصق قيمة sessionid أولاً.";
            return;
        }
        await TryLoginAsync(cookieText, persist: RememberSession);
    }

    [RelayCommand]
    private async Task LoginWithBrowserAsync()
    {
        LoginError = null;
        var cookie = await _dialogs.LoginWithBrowserAsync(_settings.ClearBrowserOnNextLogin);
        if (cookie is null) return;
        _settings.ClearBrowserOnNextLogin = false;
        SaveSettings();
        await TryLoginAsync(cookie, persist: RememberSession);
        if (!IsLoggedIn)
        {
            // The embedded browser still holds a cookie Instagram no longer accepts; without clearing it the
            // login window would close instantly and fail the same way every time.
            _settings.ClearBrowserOnNextLogin = true;
            SaveSettings();
            LoginError += "\nاضغط \"تسجيل الدخول عبر إنستجرام\" مرة أخرى لتسجيل الدخول من جديد.";
        }
    }

    private async Task TryLoginAsync(string cookieText, bool persist)
    {
        IsLoggingIn = true;
        LoginError = null;
        try
        {
            var cookies = SessionCookies.Parse(cookieText);
            var client = new InstagramClient(cookies, msg => ShowToast(msg)) { MaxRequestsPerHour = MaxRequestsPerHour };

            // The tray doubles as a session check. If only the tray endpoint misbehaves, fall back to
            // the account-info endpoint so the user can still log in and search for accounts.
            List<Models.Reel> tray;
            InstagramApiException? trayError = null;
            try
            {
                tray = await client.GetStoriesTrayAsync();
            }
            catch (InstagramApiException ex) when (ex.Kind is InstagramErrorKind.InvalidResponse or InstagramErrorKind.Unknown)
            {
                trayError = ex;
                SaveDiagnostics(ex);
                await client.GetCurrentUserAsync(); // throws if the session itself is bad
                tray = new List<Models.Reel>();
            }

            _client?.Dispose();
            _client = client;
            _downloader = new StoryDownloader(client, DownloadFolder);

            // Instagram rotates cookies while the session is used; keep the saved copy current so the
            // stored login does not go stale.
            _persistSession = persist;
            cookies.Changed += (_, _) => _dispatcher.BeginInvoke(() =>
            {
                _saveCookiesTimer.Stop();
                _saveCookiesTimer.Start();
            });
            if (persist) PersistCookies();

            IsLoggedIn = true;
            OnPropertyChanged(nameof(ViewerId));
            ApplyTray(tray);
            _lastTrayRefresh = DateTime.UtcNow;
            ScheduleTrayRefresh();
            if (AutoWatchNew) _ = SafeAsync(() => RunAutoWatchAsync());
            if (trayError is null) ShowToast("تم تسجيل الدخول بنجاح");
            else ShowToast($"تم تسجيل الدخول، لكن تعذّر جلب ستوريات من تتابعهم. يمكنك البحث عن أي حساب.\n{DiagnosticsHint}", isError: true);
        }
        catch (ArgumentException ex)
        {
            LoginError = ex.Message;
        }
        catch (InstagramApiException ex)
        {
            LoginError = SaveDiagnostics(ex) ? $"{ex.Message}\n{DiagnosticsHint}" : ex.Message;
            if (ex.Kind == InstagramErrorKind.LoginRequired && _settings.ProtectedSession is not null)
            {
                _settings.ProtectedSession = null;
                SaveSettings();
            }
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    [RelayCommand]
    private void Logout()
    {
        if (!_dialogs.Confirm("هل تريد تسجيل الخروج وحذف الجلسة المحفوظة؟")) return;
        SignOut(null);
    }

    private void SignOut(string? reason)
    {
        _trayTimer.Stop();
        _autoWatchPreviewTimer.Stop();
        _saveCookiesTimer.Stop();
        _storiesCts.Cancel();
        _lifetimeCts.Cancel();
        _lifetimeCts = new CancellationTokenSource();
        _client?.Dispose();
        _client = null;
        _downloader = null;
        _persistSession = false;
        _settings.ProtectedSession = null;
        _settings.ClearBrowserOnNextLogin = reason is null; // explicit logout → forget browser login too
        SaveSettings();

        TrayUsers.Clear();
        Stories.Clear();
        SelectedStory = null;
        CurrentUsername = null;
        AccountsWithStories = UnseenAccounts = 0;
        IsLoggedIn = false;
        LoginError = reason;
        Page = AppPage.Dashboard;
    }

    // ================================================================== tray

    [RelayCommand]
    private Task RefreshTray() => RefreshTrayAsync(silent: false);

    private async Task RefreshTrayAsync(bool silent)
    {
        if (_client is null || IsLoadingTray) return;
        IsLoadingTray = true;
        try
        {
            ApplyTray(await _client.GetStoriesTrayAsync());
            _lastTrayRefresh = DateTime.UtcNow;
            if (!silent) ShowToast("تم تحديث القائمة");
            if (AutoWatchNew) _ = SafeAsync(() => RunAutoWatchAsync());
        }
        catch (InstagramApiException ex)
        {
            await HandleErrorAsync(ex, silent);
        }
        finally
        {
            IsLoadingTray = false;
        }
    }

    private void ApplyTray(List<Models.Reel> tray)
    {
        TrayUsers.Clear();
        foreach (var reel in tray.OrderByDescending(r => r.HasUnseen).ThenByDescending(r => r.LatestReelMedia))
            TrayUsers.Add(new TrayUserViewModel(reel));
        AccountsWithStories = TrayUsers.Count;
        UnseenAccounts = TrayUsers.Count(u => u.HasUnseen);
    }

    // ================================================================== activity-aware refresh

    /// <summary>Called by the window when it is activated, deactivated, minimised or restored.</summary>
    public void SetWindowActive(bool active)
    {
        if (_isWindowActive == active) return;
        _isWindowActive = active;
        if (active && IsLoggedIn && DateTime.UtcNow - _lastTrayRefresh > TimeSpan.FromMinutes(10))
            _ = SafeAsync(() => RefreshTrayAsync(silent: true));
        ScheduleTrayRefresh();
    }

    /// <summary>
    /// Refreshes at irregular intervals, and only while the window is in use (or auto-watch is on, at a much
    /// slower pace). A fixed 5-minute loop running all day is a classic bot signature.
    /// </summary>
    private void ScheduleTrayRefresh()
    {
        _trayTimer.Stop();
        if (!IsLoggedIn) return;
        double minutes;
        if (_isWindowActive) minutes = 6 + Random.Shared.NextDouble() * 4;        // 6–10 min
        else if (AutoWatchNew) minutes = 20 + Random.Shared.NextDouble() * 10;    // 20–30 min
        else return;                                                               // idle: no traffic
        _trayTimer.Interval = TimeSpan.FromMinutes(minutes);
        _trayTimer.Start();
    }

    // ================================================================== session persistence

    private void PersistCookies()
    {
        if (!_persistSession || _client is null || _client.Cookies.IsRevoked) return;
        _settings.ProtectedSession = SettingsStore.Protect(_client.Cookies.ToHeader());
        SaveSettings();
    }
}
