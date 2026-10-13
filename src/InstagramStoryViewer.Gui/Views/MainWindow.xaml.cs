using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using InstagramStoryViewer.Gui.ViewModels;

namespace InstagramStoryViewer.Gui.Views;

public partial class MainWindow : Window, IDialogService
{
    private readonly MainViewModel _vm;
    private bool _isPlaying;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(this);
        DataContext = _vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        StateChanged += (_, _) => UpdateMaximizedState();
        Loaded += async (_, _) => await _vm.InitializeAsync();
        Closed += (_, _) => _vm.Dispose();
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
    }

    private void UpdatePlayer()
    {
        var row = _vm.IsLoggedIn ? _vm.SelectedStory : null;
        Player.Stop();
        _isPlaying = false;

        if (row is { IsVideo: true, PreviewSource: not null } &&
            Uri.TryCreate(row.PreviewSource, UriKind.Absolute, out var uri))
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            Player.Visibility = Visibility.Visible;
            PlayOverlay.Visibility = Visibility.Visible;
            MuteButton.Visibility = Visibility.Visible;
            Player.Source = uri;
            if (_vm.AutoPlayVideos) Play();
            else
            {
                // Show the first frame without starting playback.
                Player.Play();
                Player.Pause();
                SetPlayGlyph();
            }
        }
        else
        {
            Player.Source = null;
            Player.Visibility = Visibility.Collapsed;
            PlayOverlay.Visibility = Visibility.Collapsed;
            MuteButton.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Visible;
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
        await _vm.LoginWithCookieCommand.ExecuteAsync(CookieBox.Password);
        if (_vm.IsLoggedIn) CookieBox.Clear();
    }

    // ================================================================== downloads page

    private void RefreshDownloads_Click(object sender, RoutedEventArgs e) => _vm.RefreshDownloads();

    private void Downloads_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: DownloadedFileViewModel file })
            _vm.OpenFileCommand.Execute(file.FullPath);
    }

    // ================================================================== IDialogService

    public bool Confirm(string message) => ConfirmDialog.Show(this, message);

    public Task<string?> LoginWithBrowserAsync(bool clearExistingSession)
    {
        var dialog = new LoginWindow(clearExistingSession) { Owner = this };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.CookieHeader : null);
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
