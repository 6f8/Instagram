using System.Windows;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Cinemana.Desktop.ViewModels;
using Cinemana.Desktop.Views;

namespace Cinemana.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel model = CreateModel();
    private static MainViewModel CreateModel()
    {
        // UI previews use an isolated library so a running installed app retains ownership of its queue.
        if (Environment.GetCommandLineArgs().Contains("--preview"))
            return new(new Services.LibraryStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CinemanaPreview", Guid.NewGuid().ToString("N"))));
        return new();
    }
    private string? detailId;
    private PlayerView? activePlayer;
    private bool playerFullscreen;
    private WindowState stateBeforeFullscreen;
    private int playbackGeneration;
    private readonly DownloadManager downloads;
    private DownloadsWindow? downloadsWindow;
    public static readonly DependencyProperty HeroTextWidthProperty = DependencyProperty.Register(nameof(HeroTextWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(460d));
    public double HeroTextWidth { get => (double)GetValue(HeroTextWidthProperty); set => SetValue(HeroTextWidthProperty, value); }
    public MainWindow()
    {
        InitializeComponent(); DataContext = model;
        SourceInitialized += (_, _) =>
        {
            int enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int));
        };
        downloads = new DownloadManager(model.Store);
        DownloadDock.DataContext = downloads;
        SizeChanged += (_, _) => HeroTextWidth = Math.Clamp((ActualWidth - 205) * .55, 380, 560);
        model.DownloadRequested += async movie => await ShowDownloads(movie);
        Width = Math.Min(1380, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(880, SystemParameters.WorkArea.Height - 32);
        model.WatchRequested += async movie => await ShowPlayer(movie);
        Loaded += async (_, _) =>
        {
            await model.Load();
            _ = PlaybackEngine.Ready;
            var args = Environment.GetCommandLineArgs();
            int playIndex = Array.IndexOf(args, "--play");
            if (playIndex < 0 || playIndex + 1 >= args.Length || !args[playIndex + 1].All(char.IsAsciiDigit)) return;
            try
            {
                using var api = new Services.CinemanaService(model.Store.Root);
                var movie = await api.Details(args[playIndex + 1], CancellationToken.None);
                if (IsLoaded) model.WatchCommand.Execute(movie);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.IO.InvalidDataException or System.Text.Json.JsonException)
            { if (IsLoaded) MessageBox.Show(this, "تعذّر فتح العنوان المطلوب. يمكنك اختياره من المكتبة.", "سينمانا"); }
        };
        Closed += (_, _) => { playbackGeneration++; activePlayer?.Dispose(); downloads.Dispose(); model.Dispose(); PlaybackEngine.Shutdown(); };
        PreviewKeyDown += (_, e) =>
        {
            if (PlayerHost.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.F11) { TogglePlayerFullscreen(); e.Handled = true; }
                else if (e.Key == Key.Escape) { if (playerFullscreen) TogglePlayerFullscreen(); else ClosePlayer(); e.Handled = true; }
                else if (activePlayer is not null) e.Handled = activePlayer.HandleKey(e);
                return;
            }
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        };
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Heading)) CatalogScroll.ScrollToTop();
            if (e.PropertyName == nameof(MainViewModel.Selected) && model.Selected?.Id != detailId)
            {
                detailId = model.Selected?.Id;
                DetailsScroll.ScrollToTop();
                CatalogScroll.ScrollToTop();
                DetailsPanel.Visibility = Visibility.Collapsed;
            }
        };
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private async void ToggleDetailsClick(object sender, RoutedEventArgs e)
    {
        if (model.Selected is null && model.Hero is { } hero) await model.Select(hero);
        DetailsPanel.Visibility = DetailsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (DetailsPanel.Visibility == Visibility.Visible) DetailsPanel.BringIntoView();
    }
    private async void OpenDownloadsClick(object sender, RoutedEventArgs e) => await ShowDownloads(null);
    private void ClosePlayer()
    {
        playbackGeneration++;
        if (playerFullscreen) TogglePlayerFullscreen();
        activePlayer?.Dispose(); activePlayer = null;
        PlayerHost.Child = null; PlayerHost.Visibility = Visibility.Collapsed;
        Sidebar.Visibility = LibraryPanel.Visibility = Visibility.Visible;
        model.RefreshHistory();
    }
    private async Task ShowPlayer(Models.Movie movie)
    {
        ClosePlayer();
        int generation = ++playbackGeneration;
        Sidebar.Visibility = LibraryPanel.Visibility = Visibility.Hidden;
        PlayerHost.Visibility = Visibility.Visible;
        var wait = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        wait.Children.Add(new System.Windows.Controls.TextBlock { Text = "جارٍ تجهيز مشغّل الفيديو…", FontSize = 24, Margin = new Thickness(20), Foreground = System.Windows.Media.Brushes.White });
        var back = new System.Windows.Controls.Button { Content = "العودة إلى المكتبة" };
        back.Click += (_, _) => ClosePlayer(); wait.Children.Add(back); PlayerHost.Child = wait;
        try
        {
            var engine = await PlaybackEngine.Ready;
            if (generation != playbackGeneration || !IsLoaded) return;
            var player = new PlayerView(movie, model.Store, engine);
            activePlayer = player;
            player.CloseRequested += ClosePlayer;
            player.FullscreenRequested += TogglePlayerFullscreen;
            player.DownloadRequested += async selected => await ShowDownloads(selected);
            player.EscapeRequested += () => { if (playerFullscreen) TogglePlayerFullscreen(); else ClosePlayer(); };
            PlayerHost.Child = player; player.Focus();
            await player.Open(movie);
        }
        catch (Exception)
        {
            if (generation != playbackGeneration || !IsLoaded) return;
            ((System.Windows.Controls.TextBlock)wait.Children[0]).Text = "تعذّر تجهيز مشغّل الفيديو. أعد تشغيل التطبيق وحاول مجددًا.";
            PlayerHost.Child = wait;
        }
    }
    private void TogglePlayerFullscreen()
    {
        if (activePlayer is null) return;
        if (!playerFullscreen) { stateBeforeFullscreen = WindowState; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        else { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = stateBeforeFullscreen; }
        playerFullscreen = !playerFullscreen;
    }
    private async Task ShowDownloads(Models.Movie? movie)
    {
        if (downloadsWindow is null)
        {
            downloadsWindow = new DownloadsWindow(downloads, model.Store.Root) { Owner = this };
            downloadsWindow.Closed += (_, _) => downloadsWindow = null;
            downloadsWindow.Show();
        }
        else downloadsWindow.Activate();
        if (movie is not null)
        {
            int season = movie.Id == model.Selected?.Id ? model.SelectedSeason : movie.Season;
            await downloadsWindow.SetTarget(movie, season);
        }
    }
}
