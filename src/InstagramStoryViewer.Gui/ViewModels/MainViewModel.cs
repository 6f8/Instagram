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

/// <summary>UI services the view model needs from the window (keeps WPF types out of the logic).</summary>
public interface IDialogService
{
    bool Confirm(string message);
    Task<string?> LoginWithBrowserAsync(bool clearExistingSession);
    string? PickFolder(string initial);

    /// <summary>
    /// Opens each story URL in the embedded browser so Instagram records a real view.
    /// Returns how many stories were shown (in order).
    /// </summary>
    /// <param name="background">Run in the small corner window without stealing focus (auto-watch).</param>
    /// <returns>For each URL, whether the story was actually shown.</returns>
    /// <param name="onShown">Called after each story is shown, with the number shown so far in this batch.</param>
    Task<IReadOnlyList<bool>> WatchStoriesAsync(string cookieHeader, IReadOnlyList<string> storyUrls, bool background,
        Action<int>? onShown = null);

    /// <summary>Stops the watch batch that is currently playing.</summary>
    void StopWatching();

    /// <summary>Likes stories/posts in the small corner window (background, never steals focus).</summary>
    Task<IReadOnlyList<LikeOutcome>> LikeAsync(string cookieHeader, IReadOnlyList<LikeRequest> requests);

    /// <summary>Shows a Windows notification (from the notification-area icon).</summary>
    void Notify(string title, string message);
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDialogService _dialogs;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _trayTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private InstagramClient? _client;
    private StoryDownloader? _downloader;
    private readonly DispatcherTimer _autoWatchPreviewTimer;
    private readonly DispatcherTimer _saveCookiesTimer;
    private readonly DispatcherTimer _statsTimer;
    private CancellationTokenSource _storiesCts = new();
    private CancellationTokenSource _lifetimeCts = new();
    private bool _persistSession;
    private bool _isWindowActive = true;
    private DateTime _lastTrayRefresh = DateTime.MinValue;

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        _settings = SettingsStore.Load();
        _downloadFolder = _settings.DownloadFolder;
        _confirmBeforeMarkSeen = _settings.ConfirmBeforeMarkSeen;
        _autoPlayVideos = _settings.AutoPlayVideos;
        _autoWatchOnPreview = _settings.AutoWatchOnPreview;
        _autoWatchNew = _settings.AutoWatchNew;
        _autoWatchDailyLimit = Math.Clamp(_settings.AutoWatchDailyLimit, AutoWatchLimitMin, AutoWatchLimitMax);
        _maxRequestsPerHour = Math.Clamp(_settings.MaxRequestsPerHour, RequestsPerHourMin, RequestsPerHourMax);
        _quietHoursEnabled = _settings.QuietHoursEnabled;
        _quietFrom = _settings.QuietFrom;
        _quietTo = _settings.QuietTo;
        InitializeAccountLists();
        InitializeAutoLike();
        InitializeAppOptions();

        _trayTimer = new DispatcherTimer();
        _trayTimer.Tick += async (_, _) => await SafeAsync(async () =>
        {
            _trayTimer.Stop();
            await RefreshTrayAsync(silent: true);
            ScheduleTrayRefresh();
        });
        _autoWatchPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoWatchPreviewTimer.Tick += async (_, _) => await SafeAsync(async () =>
        {
            _autoWatchPreviewTimer.Stop();
            await AutoWatchSelectedAsync();
        });
        // Instagram rotates cookies in bursts; save once things settle instead of on every response.
        _saveCookiesTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _saveCookiesTimer.Tick += (_, _) =>
        {
            _saveCookiesTimer.Stop();
            PersistCookies();
        };
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _statsTimer.Tick += (_, _) => OnPropertyChanged(nameof(RequestsLastHour));
        _statsTimer.Start();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast = null; };

        Stories.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmpty));
        Downloads.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDownloads));
    }

    // ================================================================== state

    [ObservableProperty] private AppPage _page = AppPage.Dashboard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionText))]
    private bool _isLoggedIn;

    [ObservableProperty] private bool _isLoggingIn;
    [ObservableProperty] private string? _loginError;
    [ObservableProperty] private bool _rememberSession = true;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isLoadingTray;
    [ObservableProperty] private bool _isLoadingStories;

    [ObservableProperty] private bool _isPreparingPreview;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private bool _toastIsError;

    public ObservableCollection<TrayUserViewModel> TrayUsers { get; } = new();
    public ObservableCollection<StoryRowViewModel> Stories { get; } = new();
    public ObservableCollection<DownloadedFileViewModel> Downloads { get; } = new();
    public ObservableCollection<WatchRecordViewModel> History { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentUser), nameof(StoriesTitle))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAllCommand), nameof(MarkAllSeenCommand))]
    private string? _currentUsername;

    private string? _currentUserId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private StoryRowViewModel? _selectedStory;

    [ObservableProperty] private string? _emptyMessage = "اختر حساباً من القائمة أو ابحث عن مستخدم لعرض ستورياته";

    // stats
    [ObservableProperty] private int _accountsWithStories;
    [ObservableProperty] private int _unseenAccounts;
    [ObservableProperty] private int _downloadedCount;
    [ObservableProperty] private string _downloadedSize = "0 B";

    // settings
    [ObservableProperty] private string _downloadFolder;
    [ObservableProperty] private bool _confirmBeforeMarkSeen;
    [ObservableProperty] private bool _autoPlayVideos;
    [ObservableProperty] private bool _autoWatchOnPreview;
    [ObservableProperty] private bool _autoWatchNew;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AutoWatchStatus))] private int _autoWatchDailyLimit;
    [ObservableProperty] private int _maxRequestsPerHour;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AutoWatchStatus))] private bool _isAutoWatching;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(QuietHoursText))] private bool _quietHoursEnabled;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(QuietHoursText))] private int _quietFrom;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(QuietHoursText))] private int _quietTo;

    public string QuietHoursText => $"من {QuietFrom:00}:00 إلى {QuietTo:00}:00";

    public int RequestsLastHour => _client?.RequestsLastHour ?? 0;
    public bool DisclaimerAccepted => _settings.AcceptedDisclaimer;

    public void AcceptDisclaimer()
    {
        _settings.AcceptedDisclaimer = true;
        SaveSettings();
    }
    public string AutoWatchStatus =>
        $"شوهد تلقائياً اليوم: {AutoWatchedToday} من {AutoWatchDailyLimit}" + (IsAutoWatching ? " · جارٍ الآن..." : "");

    private int AutoWatchedToday
    {
        get
        {
            ResetDailyCounterIfNeeded();
            return _settings.AutoWatchedToday;
        }
    }

    public bool HasCurrentUser => CurrentUsername is not null;
    public bool ShowEmpty => Stories.Count == 0 && !IsLoadingStories;
    public bool HasDownloads => Downloads.Count > 0;
    public bool HasSelection => SelectedStory is not null;
    public string StoriesTitle => CurrentUsername is null ? "الستوريات" : $"ستوريات {CurrentUsername}";
    public string SessionText => IsLoggedIn ? "متصل" : "غير متصل";
    public string? ViewerId => _client?.ViewerId;
    public string AppVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    partial void OnDownloadFolderChanged(string value)
    {
        _settings.DownloadFolder = value;
        if (_downloader is not null) _downloader.RootFolder = value;
        SaveSettings();
        RefreshDownloads();
    }

    partial void OnConfirmBeforeMarkSeenChanged(bool value)
    {
        _settings.ConfirmBeforeMarkSeen = value;
        SaveSettings();
    }

    partial void OnAutoPlayVideosChanged(bool value)
    {
        _settings.AutoPlayVideos = value;
        SaveSettings();
    }

    partial void OnAutoWatchOnPreviewChanged(bool value)
    {
        _settings.AutoWatchOnPreview = value;
        SaveSettings();
    }

    partial void OnAutoWatchNewChanged(bool value)
    {
        _settings.AutoWatchNew = value;
        SaveSettings();
        ScheduleTrayRefresh();
        if (value) _ = SafeAsync(() => RunAutoWatchAsync());
        else if (IsAutoWatching) StopAutoWatch(); // unticking stops the running pass right away
    }

    // Typed by the user in Settings; out-of-range entries are pulled back into range.
    public const int AutoWatchLimitMin = 1, AutoWatchLimitMax = 2000;
    public const int RequestsPerHourMin = 30, RequestsPerHourMax = 600;

    partial void OnAutoWatchDailyLimitChanged(int value)
    {
        var clamped = Math.Clamp(value, AutoWatchLimitMin, AutoWatchLimitMax);
        if (clamped != value) { AutoWatchDailyLimit = clamped; return; }
        _settings.AutoWatchDailyLimit = value;
        SaveSettings();
    }

    partial void OnQuietHoursEnabledChanged(bool value)
    {
        _settings.QuietHoursEnabled = value;
        SaveSettings();
    }

    partial void OnQuietFromChanged(int value)
    {
        _settings.QuietFrom = value;
        SaveSettings();
    }

    partial void OnQuietToChanged(int value)
    {
        _settings.QuietTo = value;
        SaveSettings();
    }

    /// <summary>Steps a quiet-hours bound by ±1 hour (parameter "from+", "from-", "to+", "to-").</summary>
    [RelayCommand]
    private void StepQuietHour(string? which)
    {
        int Step(int h, int d) => (h + d + 24) % 24;
        switch (which)
        {
            case "from+": QuietFrom = Step(QuietFrom, 1); break;
            case "from-": QuietFrom = Step(QuietFrom, -1); break;
            case "to+": QuietTo = Step(QuietTo, 1); break;
            case "to-": QuietTo = Step(QuietTo, -1); break;
        }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (!_dialogs.Confirm("حذف سجل المشاهدات بالكامل؟")) return;
        WatchHistory.Clear();
        History.Clear();
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        if (AppLog.Folder is null) return;
        Directory.CreateDirectory(AppLog.Folder);
        Shell(AppLog.Folder);
    }

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (!string.IsNullOrEmpty(url)) Shell(url);
    }

    partial void OnMaxRequestsPerHourChanged(int value)
    {
        var clamped = Math.Clamp(value, RequestsPerHourMin, RequestsPerHourMax);
        if (clamped != value) { MaxRequestsPerHour = clamped; return; }
        _settings.MaxRequestsPerHour = value;
        if (_client is not null) _client.MaxRequestsPerHour = value;
        SaveSettings();
    }

    partial void OnSelectedStoryChanged(StoryRowViewModel? value)
    {
        _autoWatchPreviewTimer.Stop();
        if (AutoWatchOnPreview && value is { MarkedSeen: false } && IsLoggedIn)
            _autoWatchPreviewTimer.Start(); // only if the user stays on it for a moment
    }

    partial void OnIsLoadingStoriesChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    partial void OnPageChanged(AppPage value)
    {
        if (value == AppPage.Downloads) RefreshDownloads();
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Error("Unexpected error in background task", ex);
            ShowToast($"خطأ غير متوقع: {ex.Message}", isError: true);
        }
    }

    // ================================================================== helpers

    private static string DiagnosticsPath => Path.Combine(SettingsStore.AppDataFolder, "last-response.txt");
    private static string DiagnosticsHint => $"حُفظت استجابة الخادم في: {DiagnosticsPath}";

    /// <summary>Writes the unexpected server response to disk so it can be inspected or shared.</summary>
    private static bool SaveDiagnostics(InstagramApiException ex)
    {
        if (ex.ResponseBody is null) return false;
        try
        {
            Directory.CreateDirectory(SettingsStore.AppDataFolder);
            // Tokens and account ids are masked so the file is safe to share.
            File.WriteAllText(DiagnosticsPath,
                $"Time: {DateTime.Now:u}\nRequest: {DiagnosticsSanitizer.Sanitize(ex.RequestPath)}\nStatus: {(int?)ex.StatusCode}\n" +
                $"Content-Type: {ex.ContentType}\nError: {DiagnosticsSanitizer.Sanitize(ex.Message)}\n\n" +
                $"----- response body (sensitive values masked) -----\n{DiagnosticsSanitizer.Sanitize(ex.ResponseBody)}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task HandleErrorAsync(InstagramApiException ex, bool silent)
    {
        AppLog.Warn($"API error: {ex.Kind} HTTP {(int?)ex.StatusCode} {ex.RequestPath} {ex.Message}");
        var saved = SaveDiagnostics(ex);
        if (ex.Kind is InstagramErrorKind.LoginRequired or InstagramErrorKind.Checkpoint)
        {
            // One endpoint rejecting the session does not mean it has expired: confirm before
            // signing out, otherwise a single misbehaving endpoint would wipe a working login.
            if (_client is not null && !await IsSessionAliveAsync(_client))
            {
                SignOut(ex.Message);
                return;
            }
            if (!silent) ShowToast("رفض إنستجرام هذا الطلب، لكن جلستك ما زالت صالحة." + (saved ? $"\n{DiagnosticsHint}" : ""), isError: true);
            return;
        }
        if (!silent) ShowToast(saved ? $"{ex.Message}\n{DiagnosticsHint}" : ex.Message, isError: true);
    }

    /// <summary>
    /// The session counts as dead only when two independent endpoints both say "login required"; anything
    /// else (HTML pages, network errors, rate limits) is not proof that the user was logged out.
    /// </summary>
    private static async Task<bool> IsSessionAliveAsync(InstagramClient client)
    {
        foreach (var check in new Func<Task>[] { () => client.GetStoriesTrayAsync(), () => client.GetCurrentUserAsync() })
        {
            try
            {
                await check();
                return true;
            }
            catch (InstagramApiException ex) when (ex.Kind is InstagramErrorKind.LoginRequired or InstagramErrorKind.Checkpoint)
            {
                // try the next endpoint
            }
            catch (InstagramApiException)
            {
                return true;
            }
        }
        return false;
    }

    public void ShowToast(string message, bool isError = false)
    {
        ToastIsError = isError;
        Toast = message;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void SaveSettings()
    {
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"تعذّر حفظ الإعدادات: {ex.Message}", isError: true);
        }
    }

    private void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowToast($"تعذّر الفتح: {ex.Message}", isError: true);
        }
    }

    public void Dispose()
    {
        _trayTimer.Stop();
        _statsTimer.Stop();
        _autoWatchPreviewTimer.Stop();
        if (_saveCookiesTimer.IsEnabled)
        {
            _saveCookiesTimer.Stop();
            PersistCookies(); // flush a pending cookie rotation before exit
        }
        _lifetimeCts.Cancel();
        _storiesCts.Cancel();
        _storiesCts.Dispose();
        _client?.Dispose();
    }
}
