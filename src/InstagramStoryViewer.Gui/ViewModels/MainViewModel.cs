using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstagramStoryViewer.Api;
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
    Task<int> WatchStoriesAsync(string cookieHeader, IReadOnlyList<string> storyUrls);
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDialogService _dialogs;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _trayTimer;
    private readonly DispatcherTimer _toastTimer;
    private InstagramClient? _client;
    private StoryDownloader? _downloader;
    private CancellationTokenSource _storiesCts = new();
    private string? _sessionCookieHeader;

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        _settings = SettingsStore.Load();
        _downloadFolder = _settings.DownloadFolder;
        _confirmBeforeMarkSeen = _settings.ConfirmBeforeMarkSeen;
        _autoPlayVideos = _settings.AutoPlayVideos;

        _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _trayTimer.Tick += async (_, _) => await RefreshTrayAsync(silent: true);
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
            await _client.DownloadAsync(row.MediaUrl, path, ct);
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
            await TryLoginAsync(saved, persist: false);
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
            var client = new InstagramClient(cookies, msg => ShowToast(msg));

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
            _sessionCookieHeader = cookies.ToHeader(); // normalised "name=value; ..." form
            _downloader = new StoryDownloader(client, DownloadFolder);

            if (persist)
            {
                _settings.ProtectedSession = SettingsStore.Protect(cookieText.Trim());
                SaveSettings();
            }

            IsLoggedIn = true;
            OnPropertyChanged(nameof(ViewerId));
            ApplyTray(tray);
            _trayTimer.Start();
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
        _storiesCts.Cancel();
        _client?.Dispose();
        _client = null;
        _downloader = null;
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
            if (!silent) ShowToast("تم تحديث القائمة");
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
                var path = _downloader!.GetPath(username, item);
                if (File.Exists(path))
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
            var path = await _downloader.DownloadAsync(row.Username, row.Item, CancellationToken.None, progress);
            row.LocalPath = path;
            row.SizeText = Format.Size(new FileInfo(path).Length);
            row.State = DownloadState.Done;
            return true;
        }
        catch (Exception ex) when (ex is InstagramApiException or HttpRequestException or IOException or TaskCanceledException
                                       or UnauthorizedAccessException)
        {
            row.State = DownloadState.Failed;
            row.Error = ex.Message;
            ShowToast($"فشل التنزيل: {ex.Message}", isError: true);
            return false;
        }
    }

    public void RefreshDownloads()
    {
        Downloads.Clear();
        long total = 0;
        try
        {
            if (Directory.Exists(DownloadFolder))
            {
                var root = new DirectoryInfo(DownloadFolder);
                var files = root.EnumerateFiles("*.*", SearchOption.AllDirectories)
                    .Where(f => f.Extension is ".jpg" or ".mp4")
                    .OrderByDescending(f => f.LastWriteTimeUtc);
                foreach (var f in files)
                {
                    Downloads.Add(new DownloadedFileViewModel(f, DownloadFolder));
                    total += f.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"تعذّر قراءة مجلد التنزيل: {ex.Message}", isError: true);
        }
        DownloadedCount = Downloads.Count;
        DownloadedSize = Format.Size(total);
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

    private async Task<int> WatchRowsAsync(IReadOnlyList<StoryRowViewModel> rows)
    {
        if (_sessionCookieHeader is null) return 0;
        var urls = rows.Select(r => StoryUrl(r)).ToList();
        var watched = await _dialogs.WatchStoriesAsync(_sessionCookieHeader, urls);
        for (var i = 0; i < Math.Min(watched, rows.Count); i++)
            rows[i].MarkedSeen = true;
        return watched;
    }

    private static string StoryUrl(StoryRowViewModel row)
    {
        var pk = row.Item.Pk ?? row.Item.Id?.Split('_')[0];
        return $"https://www.instagram.com/stories/{Uri.EscapeDataString(row.Username)}/{pk}/";
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
            File.WriteAllText(DiagnosticsPath,
                $"Time: {DateTime.Now:u}\nRequest: {ex.RequestPath}\nStatus: {(int?)ex.StatusCode}\nContent-Type: {ex.ContentType}\n" +
                $"Error: {ex.Message}\n\n----- response body -----\n{ex.ResponseBody}");
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
        _storiesCts.Cancel();
        _storiesCts.Dispose();
        _client?.Dispose();
    }
}
