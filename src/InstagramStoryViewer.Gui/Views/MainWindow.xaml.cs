using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using InstagramStoryViewer.Gui.Services;
using InstagramStoryViewer.Gui.ViewModels;

namespace InstagramStoryViewer.Gui.Views;

public partial class MainWindow : Window, IDialogService
{
    private readonly MainViewModel _vm;
    private readonly TrayIcon _tray;
    private bool _isPlaying;
    private bool _started;
    private bool _exitRequested;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(this);
        DataContext = _vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        StateChanged += (_, _) =>
        {
            UpdateMaximizedState();
            // Minimised = not in use: the view model slows or stops background refreshes.
            _vm.SetWindowActive(WindowState != WindowState.Minimized);
        };
        Loaded += async (_, _) => await OnLoadedAsync();

        _tray = new TrayIcon();
        _tray.SetAutoWatch(_vm.AutoWatchNew);
        _tray.OpenRequested += ShowFromTray;
        _tray.RefreshRequested += () => _vm.RefreshTrayCommand.Execute(null);
        _tray.AutoWatchToggled += on => _vm.AutoWatchNew = on;
        _tray.ExitRequested += ExitApp;

        Closing += OnClosing;
        Closed += (_, _) =>
        {
            if (_watcher is { IsAlive: true }) _watcher.Close();
            _tray.Dispose();
            _vm.Dispose();
        };
    }

    /// <summary>The first-run notice needs the window, so starting hidden is only possible after it.</summary>
    public bool CanStartHidden => _vm.DisclaimerAccepted;

    /// <summary>Started with Windows (--minimized): run in the notification area without showing the window.</summary>
    public void StartHidden()
    {
        _vm.SetWindowActive(false);
        _ = OnLoadedAsync();
    }

    /// <summary>Shows the window from the notification area (also used when a second copy is started).</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _vm.SetWindowActive(true);
    }

    private void ExitApp()
    {
        _exitRequested = true;
        Close();
    }

    /// <summary>Closing the window keeps the app running in the notification area (when enabled).</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested || !_vm.MinimizeToTray) return;
        e.Cancel = true;
        Hide();
        _vm.SetWindowActive(false);
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.Notify("البرنامج يعمل في الخلفية", "للخروج نهائياً: انقر بالزر الأيمن على الأيقونة ثم \"خروج\".");
        }
    }

    private async Task OnLoadedAsync()
    {
        if (_started) return;
        _started = true;
        if (!ShowDisclaimerIfNeeded())
        {
            ExitApp();
            return;
        }
        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _vm.ShowToast($"تعذّر بدء البرنامج بشكل كامل: {ex.Message}", isError: true);
        }
    }

    /// <summary>First start only: explains the risks of using unofficial Instagram endpoints.</summary>
    private bool ShowDisclaimerIfNeeded()
    {
        if (_vm.DisclaimerAccepted) return true;
        var ok = ConfirmDialog.Show(this,
            "تنبيه قبل البدء:\n\n" +
            "• هذا البرنامج غير تابع لإنستجرام ويستخدم واجهاته غير الرسمية، وقد يخالف ذلك شروط الاستخدام.\n" +
            "• الإفراط في الاستخدام قد يؤدي إلى تقييد حسابك مؤقتاً؛ البرنامج يحدّ عدد الطلبات تلقائياً لتقليل ذلك.\n" +
            "• احترم خصوصية الآخرين ولا تعِد نشر محتواهم دون إذن.\n\n" +
            "باستخدامك البرنامج فأنت تتحمّل مسؤولية ذلك. هل تريد المتابعة؟");
        if (ok) _vm.AcceptDisclaimer();
        return ok;
    }

    // ================================================================== window chrome

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizedState()
    {
        // A maximized chrome-less window overhangs the screen by the resize border; compensate.
        var max = WindowState == WindowState.Maximized;
        RootBorder.Margin = max ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = max ? "\uE3E0" : "\uE3C6"; // restore / maximize
    }

    // ================================================================== preview player

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedStory) or nameof(MainViewModel.IsLoggedIn))
            UpdatePlayer();
        if (e.PropertyName == nameof(MainViewModel.AutoWatchNew))
            _tray.SetAutoWatch(_vm.AutoWatchNew);
    }

    private CancellationTokenSource? _previewCts;

    private async void UpdatePlayer()
    {
        try
        {
            await UpdatePlayerAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _vm.ShowToast($"تعذّر عرض المعاينة: {ex.Message}", isError: true);
        }
    }

    private async Task UpdatePlayerAsync()
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();

        var row = _vm.IsLoggedIn ? _vm.SelectedStory : null;
        Player.Stop();
        Player.Source = null;
        Player.Visibility = Visibility.Collapsed;
        _isPlaying = false;

        // The poster image (cover frame) stays visible until the video actually starts.
        PreviewImage.Visibility = Visibility.Visible;
        var isVideo = row is { IsVideo: true };
        PlayOverlay.Visibility = Visibility.Collapsed;
        MuteButton.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        if (!isVideo) return;

        var path = await _vm.PrepareVideoAsync(row!, cts.Token);
        if (cts.IsCancellationRequested || path is null || !ReferenceEquals(row, _vm.SelectedStory)) return;

        Player.Source = new Uri(path);
        Player.Visibility = Visibility.Visible;
        PlayOverlay.Visibility = Visibility.Visible;
        if (_vm.AutoPlayVideos) Play();
        else
        {
            // Show the first frame without starting playback.
            Player.Play();
            Player.Pause();
            SetPlayGlyph();
        }
    }

    private void Play()
    {
        Player.Play();
        _isPlaying = true;
        SetPlayGlyph();
    }

    private void SetPlayGlyph()
    {
        if (PlayOverlay.Template.FindName("PlayBadge", PlayOverlay) is Border badge)
            badge.Visibility = _isPlaying ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            Player.Pause();
            _isPlaying = false;
            SetPlayGlyph();
        }
        else Play();
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        Player.IsMuted = !Player.IsMuted;
        MuteGlyph.Text = Player.IsMuted ? "\uE04F" : "\uE050"; // volume_off / volume_up
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e) => SetPlayGlyph();

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        // Loop like Instagram does.
        Player.Position = TimeSpan.Zero;
        Player.Play();
    }

    private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _isPlaying = false;
        SetPlayGlyph();
        _vm.ShowToast("تعذّر تشغيل الفيديو هنا، جرّب تنزيله أو فتحه في المتصفح.", isError: true);
    }

    // ================================================================== login

    private void CookieLogin_Click(object sender, RoutedEventArgs e) => SubmitCookie();

    private void CookieBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SubmitCookie();
    }

    private async void SubmitCookie()
    {
        try
        {
            await _vm.LoginWithCookieCommand.ExecuteAsync(CookieBox.Password);
            if (_vm.IsLoggedIn) CookieBox.Clear();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _vm.ShowToast($"تعذّر تسجيل الدخول: {ex.Message}", isError: true);
        }
    }

    // ================================================================== IDialogService

    public bool Confirm(string message) => ConfirmDialog.Show(this, message);

    public Task<string?> LoginWithBrowserAsync(bool clearExistingSession)
    {
        var dialog = new LoginWindow(clearExistingSession) { Owner = this };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.CookieHeader : null);
    }

    private readonly SemaphoreSlim _watchGate = new(1, 1);
    private StoryWatchWindow? _watcher;

    /// <summary>
    /// Plays stories in the small corner window. Batches (manual and automatic) run one after another in the
    /// same window. It has no Owner on purpose, so background watching keeps working while this window is
    /// minimised.
    /// </summary>
    public async Task<IReadOnlyList<bool>> WatchStoriesAsync(string cookieHeader, IReadOnlyList<string> storyUrls, bool background)
    {
        await _watchGate.WaitAsync();
        try
        {
            if (_watcher is not { IsAlive: true })
            {
                _watcher = new StoryWatchWindow();
                _watcher.PlaceInCorner();
            }
            if (!_watcher.IsVisible) _watcher.Show();
            if (!background) _watcher.Activate();

            return await _watcher.PlayAsync(cookieHeader, storyUrls);
        }
        finally
        {
            // Close when nothing else is queued, so the browser does not linger in memory.
            if (_watchGate.CurrentCount == 0 && _watcher is { IsAlive: true } w)
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (_watchGate.CurrentCount == 1 && w.IsAlive) w.Close();
                }, System.Windows.Threading.DispatcherPriority.Background);
            _watchGate.Release();
        }
    }

    /// <summary>In-app toast while the window is in front, Windows notification otherwise.</summary>
    public void Notify(string title, string message)
    {
        if (IsVisible && IsActive && WindowState != WindowState.Minimized)
            _vm.ShowToast($"{title}: {message}");
        else
            _tray.Notify(title, message);
    }

    public string? PickFolder(string initial)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "اختر مجلد التنزيل",
            InitialDirectory = Directory.Exists(initial) ? initial : null
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
}
