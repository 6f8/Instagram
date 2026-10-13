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
    Task<int> WatchStoriesAsync(string cookieHeader, IReadOnlyList<string> storyUrls, bool background);
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
        _autoWatchDailyLimit = _settings.AutoWatchDailyLimit;
        _maxRequestsPerHour = _settings.MaxRequestsPerHour;

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
    }

    partial void OnAutoWatchDailyLimitChanged(int value)
    {
        _settings.AutoWatchDailyLimit = value;
        SaveSettings();
    }

    partial void OnMaxRequestsPerHourChanged(int value)
    {
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

    // ================================================================== startup / login

    private static string VideoCacheFolder => Path.Combine(Path.GetTempPath(), "InstagramStoryViewer", "video-cache");

    /// <summary>
    /// Windows' media player often cannot stream Instagram's CDN URLs directly, so videos are fetched to a
    /// temp file first (or the downloaded copy is used). Returns null when the video could not be fetched.
    /// </summary>
    public async Task<string?> PrepareVideoAsync(StoryRowViewModel row, CancellationToken ct)
    {
        if (row.LocalPath is not null && File.Exists(row.LocalPath)) return row.LocalPath;
        if (_client is null || row.MediaUrl is null) return null;

        var name = string.Concat((row.Item.Pk ?? row.Item.Id ?? Guid.NewGuid().ToString()).Where(char.IsLetterOrDigit));
        var path = Path.Combine(VideoCacheFolder, name + ".mp4");
        if (File.Exists(path)) return path;

        IsPreparingPreview = true;
        try
        {
            Directory.CreateDirectory(VideoCacheFolder);
            try
            {
                await _client.DownloadAsync(row.MediaUrl, path, ct);
            }
            catch (InstagramApiException ex) when (ex.Kind == InstagramErrorKind.MediaExpired)
            {
                if (!await RefreshRowAsync(row, ct)) throw;
                await _client.DownloadAsync(row.MediaUrl!, path, ct);
            }
            return path;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is InstagramApiException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            ShowToast($"تعذّر تحميل الفيديو للمعاينة: {ex.Message}", isError: true);
            return null;
        }
        finally
        {
            IsPreparingPreview = false;
        }
    }

    private static void CleanVideoCache()
    {
        try
        {
            if (!Directory.Exists(VideoCacheFolder)) return;
            foreach (var f in new DirectoryInfo(VideoCacheFolder).EnumerateFiles())
                if (f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) f.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; stories expire anyway
        }
    }

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

    // ================================================================== stories

    [RelayCommand]
    private async Task SearchAsync()
    {
        var name = SearchText.Trim().TrimStart('@');
        if (name.Length == 0 || _client is null) return;
        var cts = ResetStoriesCts();
        await RunStoriesLoadAsync(name, async ct =>
        {
            var profile = await _client.GetProfileAsync(name, ct);
            if (profile.IsPrivate && !profile.FollowedByViewer && profile.Id != _client.ViewerId)
            {
                EmptyMessage = $"{profile.Username} حساب خاص ولا تتابعه، لا يمكن عرض ستورياته.";
                return null;
            }
            return (profile.Username ?? name, profile.Id!);
        }, cts.Token);
    }

    [RelayCommand]
    private async Task OpenUserAsync(TrayUserViewModel? user)
    {
        if (user is null || _client is null) return;
        Page = AppPage.Dashboard;
        var cts = ResetStoriesCts();
        await RunStoriesLoadAsync(user.Username, _ => Task.FromResult<(string, string)?>((user.Username, user.UserId)), cts.Token);
    }

    private CancellationTokenSource ResetStoriesCts()
    {
        // Cancel (not dispose) the previous load: its in-flight request may still hold the token.
        _storiesCts.Cancel();
        _storiesCts = new CancellationTokenSource();
        return _storiesCts;
    }

    private async Task RunStoriesLoadAsync(string displayName, Func<CancellationToken, Task<(string Username, string UserId)?>> resolve,
        CancellationToken ct)
    {
        Stories.Clear();
        SelectedStory = null;
        CurrentUsername = displayName;
        _currentUserId = null;
        EmptyMessage = null;
        IsLoadingStories = true;
        try
        {
            var target = await resolve(ct);
            if (target is null) return;
            var (username, userId) = target.Value;
            CurrentUsername = username;
            _currentUserId = userId;

            var reel = await _client!.GetUserStoriesAsync(userId, ct);
            ct.ThrowIfCancellationRequested();
            if (reel?.Items is null)
            {
                EmptyMessage = $"لا توجد ستوريات حالية لدى {username}";
                return;
            }

            var i = 0;
            foreach (var item in reel.Items)
            {
                var row = new StoryRowViewModel(item, username, userId, ++i);
                var path = _downloader!.FindExistingPath(username, item);
                if (path is not null)
                {
                    row.LocalPath = path;
                    row.SizeText = Format.Size(new FileInfo(path).Length);
                    row.State = DownloadState.Done;
                }
                Stories.Add(row);
            }
            SelectedStory = Stories.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            // a newer load replaced this one
        }
        catch (InstagramApiException ex)
        {
            EmptyMessage = ex.Message;
            await HandleErrorAsync(ex, silent: false);
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoadingStories = false;
        }
    }

    // ================================================================== downloads

    [RelayCommand]
    private async Task DownloadStoryAsync(StoryRowViewModel? row)
    {
        row ??= SelectedStory;
        if (row is null) return;
        await DownloadRowAsync(row);
        RefreshDownloads();
    }

    [RelayCommand(CanExecute = nameof(HasCurrentUser))]
    private async Task DownloadAllAsync()
    {
        var pending = Stories.Where(s => s.State is DownloadState.None or DownloadState.Failed).ToList();
        if (pending.Count == 0)
        {
            ShowToast("كل الستوريات منزّلة مسبقاً");
            return;
        }
        foreach (var r in pending) r.State = DownloadState.Queued;
        var ok = 0;
        foreach (var r in pending)
            if (await DownloadRowAsync(r)) ok++;
        RefreshDownloads();
        ShowToast($"تم تنزيل {ok} من {pending.Count}", isError: ok < pending.Count);
    }

    private async Task<bool> DownloadRowAsync(StoryRowViewModel row)
    {
        if (_downloader is null || row.State is DownloadState.Downloading or DownloadState.Done) return row.State == DownloadState.Done;
        row.State = DownloadState.Downloading;
        row.Progress = 0;
        row.Error = null;
        try
        {
            var progress = new Progress<double>(p => row.Progress = p * 100);
            string path;
            try
            {
                path = await _downloader.DownloadAsync(row.Username, row.Item, _lifetimeCts.Token, progress);
            }
            catch (InstagramApiException ex) when (ex.Kind == InstagramErrorKind.MediaExpired)
            {
                // The CDN link expired while the list was open: fetch a fresh one and retry once.
                if (!await RefreshRowAsync(row, _lifetimeCts.Token)) throw;
                path = await _downloader.DownloadAsync(row.Username, row.Item, _lifetimeCts.Token, progress);
            }
            row.LocalPath = path;
            row.SizeText = Format.Size(new FileInfo(path).Length);
            row.State = DownloadState.Done;
            return true;
        }
        catch (OperationCanceledException)
        {
            row.State = DownloadState.None; // cancelled by sign-out / exit, not a failure
            return false;
        }
        catch (Exception ex) when (ex is InstagramApiException or HttpRequestException or IOException
                                       or UnauthorizedAccessException)
        {
            row.State = DownloadState.Failed;
            row.Error = ex.Message;
            ShowToast($"فشل التنزيل: {ex.Message}", isError: true);
            return false;
        }
    }

    private int _downloadsScanVersion;

    /// <summary>Rescans the download folder on a worker thread so large folders do not freeze the window.</summary>
    public async void RefreshDownloads()
    {
        var version = ++_downloadsScanVersion;
        var folder = DownloadFolder;
        try
        {
            var files = await Task.Run(() =>
            {
                if (!Directory.Exists(folder)) return new List<DownloadedFileViewModel>();
                return new DirectoryInfo(folder).EnumerateFiles("*.*", SearchOption.AllDirectories)
                    .Where(f => f.Extension is ".jpg" or ".mp4")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => new DownloadedFileViewModel(f, folder))
                    .ToList();
            });
            if (version != _downloadsScanVersion) return; // a newer scan superseded this one

            Downloads.Clear();
            foreach (var f in files) Downloads.Add(f);
            DownloadedCount = files.Count;
            DownloadedSize = Format.Size(files.Sum(f => f.SizeBytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"تعذّر قراءة مجلد التنزيل: {ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private void OpenFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        Shell(path);
    }

    [RelayCommand]
    private void ShowInFolder(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { ShowToast(ex.Message, true); }
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        Directory.CreateDirectory(DownloadFolder);
        Shell(DownloadFolder);
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var picked = _dialogs.PickFolder(DownloadFolder);
        if (picked is not null) DownloadFolder = picked;
    }

    [RelayCommand]
    private void OpenInBrowser(StoryRowViewModel? row)
    {
        row ??= SelectedStory;
        if (row?.MediaUrl is not null) Shell(row.MediaUrl);
    }

    // ================================================================== seen

    // Instagram retired the REST "seen" endpoints (they return 404), and the web app now uses internal
    // GraphQL calls whose ids change all the time. A story is therefore marked as seen the way a person
    // does it: by actually showing it in the embedded browser.

    [RelayCommand]
    private async Task MarkSeenAsync(StoryRowViewModel? row)
    {
        row ??= SelectedStory;
        if (row is null || _client is null) return;
        if (ConfirmBeforeMarkSeen && !_dialogs.Confirm($"سيُفتح الستوري في المتصفح المدمج وسيظهر اسمك في قائمة مشاهديه لدى {row.Username}. متابعة؟"))
            return;
        var ok = await WatchRowsAsync([row]);
        if (ok > 0) ShowToast("تمت مشاهدة الستوري");
        UpdateTraySeen(row.Username);
    }

    [RelayCommand(CanExecute = nameof(HasCurrentUser))]
    private async Task MarkAllSeenAsync()
    {
        if (_client is null || Stories.Count == 0) return;
        var pending = Stories.Where(s => !s.MarkedSeen).ToList();
        if (pending.Count == 0)
        {
            ShowToast("كل الستوريات مشاهَدة مسبقاً");
            return;
        }
        if (ConfirmBeforeMarkSeen && !_dialogs.Confirm($"ستُعرض {pending.Count} ستوري في المتصفح المدمج وسيظهر اسمك في قائمة مشاهدي {CurrentUsername}. متابعة؟"))
            return;
        var ok = await WatchRowsAsync(pending);
        ShowToast($"تمت مشاهدة {ok} من {pending.Count}", isError: ok < pending.Count);
        if (CurrentUsername is not null) UpdateTraySeen(CurrentUsername);
    }

    private async Task<int> WatchRowsAsync(IReadOnlyList<StoryRowViewModel> rows, bool background = false)
    {
        if (_client is null) return 0;
        var urls = rows.Select(r => StoryUrl(r)).ToList();
        var watched = await _dialogs.WatchStoriesAsync(_client.Cookies.ToHeader(), urls, background);
        for (var i = 0; i < Math.Min(watched, rows.Count); i++)
            rows[i].MarkedSeen = true;
        return watched;
    }

    private static string StoryUrl(StoryRowViewModel row) => StoryUrl(row.Username, row.Item);

    private static string StoryUrl(string username, Models.StoryItem item)
    {
        var pk = item.Pk ?? item.Id?.Split('_')[0];
        return $"https://www.instagram.com/stories/{Uri.EscapeDataString(username)}/{pk}/";
    }

    // ================================================================== auto-watch

    private void ResetDailyCounterIfNeeded()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_settings.AutoWatchDay == today) return;
        _settings.AutoWatchDay = today;
        _settings.AutoWatchedToday = 0;
    }

    private int AutoWatchBudget => Math.Max(0, AutoWatchDailyLimit - AutoWatchedToday);

    private void CountAutoWatched(int n)
    {
        if (n <= 0) return;
        ResetDailyCounterIfNeeded();
        _settings.AutoWatchedToday += n;
        SaveSettings();
        OnPropertyChanged(nameof(AutoWatchStatus));
    }

    /// <summary>"Watch on preview": the user lingered on an unwatched story.</summary>
    private async Task AutoWatchSelectedAsync()
    {
        var row = SelectedStory;
        if (row is null || row.MarkedSeen || !AutoWatchOnPreview || IsAutoWatching || AutoWatchBudget == 0) return;
        var n = await WatchRowsAsync([row], background: true);
        CountAutoWatched(n);
        UpdateTraySeen(row.Username);
    }

    /// <summary>
    /// Background auto-watch: plays the unseen stories of followed accounts in the corner window, within
    /// the daily limit. Stories newer than the reel's "seen" marker are the unseen ones.
    /// </summary>
    private async Task RunAutoWatchAsync()
    {
        if (!AutoWatchNew || IsAutoWatching || _client is null) return;
        var budget = AutoWatchBudget;
        if (budget == 0) return;

        var targets = TrayUsers.Where(u => u.HasUnseen).Take(20).ToList();
        if (targets.Count == 0) return;

        IsAutoWatching = true;
        try
        {
            var reels = await _client.GetStoriesForUsersAsync(targets.Select(t => t.UserId), _lifetimeCts.Token);
            var queue = new List<(TrayUserViewModel User, string Url)>();
            foreach (var reel in reels)
            {
                var user = targets.FirstOrDefault(t => t.UserId == (reel.User?.Pk ?? reel.Id));
                if (user is null) continue;
                var seen = reel.Seen ?? 0;
                foreach (var item in reel.Items!.Where(i => i.TakenAt > seen))
                    queue.Add((user, StoryUrl(user.Username, item)));
            }
            queue = queue.Take(budget).ToList();
            if (queue.Count == 0) return;

            var watched = await _dialogs.WatchStoriesAsync(_client.Cookies.ToHeader(), queue.Select(q => q.Url).ToList(), background: true);
            CountAutoWatched(watched);

            // Accounts whose every queued story was shown are now fully seen.
            foreach (var group in queue.Take(watched).GroupBy(q => q.User))
                if (queue.Count(q => q.User == group.Key) == group.Count())
                    group.Key.HasUnseen = false;
            UnseenAccounts = TrayUsers.Count(u => u.HasUnseen);
            if (watched > 0) ShowToast($"المشاهدة التلقائية: شوهد {watched} ستوري");
        }
        catch (InstagramApiException ex)
        {
            await HandleErrorAsync(ex, silent: true);
        }
        catch (OperationCanceledException)
        {
            // signed out
        }
        finally
        {
            IsAutoWatching = false;
        }
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

    /// <summary>Re-fetches a story whose CDN link expired and swaps in the fresh item.</summary>
    private async Task<bool> RefreshRowAsync(StoryRowViewModel row, CancellationToken ct)
    {
        if (_client is null) return false;
        try
        {
            var reel = await _client.GetUserStoriesAsync(row.OwnerId, ct);
            var fresh = reel?.Items?.FirstOrDefault(i => i.Pk == row.Item.Pk);
            if (fresh is null) return false;
            row.ReplaceItem(fresh);
            return true;
        }
        catch (InstagramApiException)
        {
            return false;
        }
    }

    /// <summary>Runs fire-and-forget UI work without letting an unexpected exception crash the app.</summary>
    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowToast($"خطأ غير متوقع: {ex.Message}", isError: true);
        }
    }

    private void UpdateTraySeen(string username)
    {
        if (Stories.Count == 0 || Stories.Any(s => !s.MarkedSeen)) return;
        var user = TrayUsers.FirstOrDefault(u => u.Username == username);
        if (user is { HasUnseen: true })
        {
            user.HasUnseen = false;
            UnseenAccounts = TrayUsers.Count(u => u.HasUnseen);
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
