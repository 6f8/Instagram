using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Cinemana.Desktop.Models;
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
            return new(new Services.LibraryStore(Path.Combine(Path.GetTempPath(), "CinemanaPreview", Guid.NewGuid().ToString("N"))));
        return new();
    }
    private string? detailId;
    private PlayerView? activePlayer;
    private bool playerFullscreen;
    private WindowState stateBeforeFullscreen;
    private int playbackGeneration;
    private readonly DownloadManager downloads;
    private DownloadsWindow? downloadsWindow;
    private readonly DispatcherTimer carousel = new() { Interval = TimeSpan.FromSeconds(8) };
    private System.Windows.Forms.NotifyIcon? tray;
    public static readonly DependencyProperty HeroTextWidthProperty = DependencyProperty.Register(nameof(HeroTextWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(560d));
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
        DownloadBadge.DataContext = downloads;
        downloads.Completed += ShowCompleted;
        model.DownloadSettingsChanged += downloads.Start;
        model.LibraryProvider = () => downloads.Library
            .GroupBy(i => i.Destination, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(i => i.Spec.Movie with { LocalFile = i.Destination, ProgressText = Path.GetFileName(i.Destination) });
        SizeChanged += (_, _) => HeroTextWidth = Math.Clamp((ActualWidth - 262) * .55, 420, 680);
        model.DownloadRequested += async movie => await ShowDownloads(movie);
        Width = Math.Min(1500, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(940, SystemParameters.WorkArea.Height - 32);
        model.WatchRequested += async movie => await ShowPlayer(movie);
        // The banner advances on its own while the home page is shown and nothing is selected.
        carousel.Tick += (_, _) => { if (model.IsHome && PlayerHost.Visibility != Visibility.Visible && IsActive) model.MoveHero(1); };
        carousel.Start();
        Loaded += async (_, _) =>
        {
            await model.Load();
            var args = Environment.GetCommandLineArgs();
            int playIndex = Array.IndexOf(args, "--play");
            if (playIndex < 0 || playIndex + 1 >= args.Length || !args[playIndex + 1].All(char.IsAsciiDigit)) return;
            try
            {
                using var api = new Services.CinemanaService(model.Store.Root);
                var movie = await api.Details(args[playIndex + 1], CancellationToken.None);
                if (IsLoaded) model.WatchCommand.Execute(movie);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
            { if (IsLoaded) MessageBox.Show(this, "تعذّر فتح العنوان المطلوب. يمكنك اختياره من المكتبة.", "سينمانا"); }
        };
        Closed += (_, _) => { playbackGeneration++; carousel.Stop(); activePlayer?.Dispose(); downloads.Dispose(); model.Dispose(); tray?.Dispose(); };
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

    private void ShowCompleted(DownloadItem item)
    {
        if (!model.NotifyOnComplete) return;
        try
        {
            tray ??= new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Text = "سينمانا", Visible = true };
            tray.BalloonTipClicked -= TrayClicked; tray.BalloonTipClicked += TrayClicked;
            tray.ShowBalloonTip(5000, "اكتمل التنزيل", item.Title, System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception e) when (e is ArgumentException or IOException or System.ComponentModel.Win32Exception) { }
    }
    private async void TrayClicked(object? sender, EventArgs e) { Activate(); await ShowDownloads(null); }
    private void RowWheel(object sender, MouseWheelEventArgs e)
    {
        // Rows scroll sideways with the wheel; the page keeps scrolling once a row reaches its end.
        if (sender is not ScrollViewer row || row.ScrollableWidth <= 0) return;
        double target = row.HorizontalOffset - e.Delta;
        if (target < 0 && row.HorizontalOffset <= 0 || target > row.ScrollableWidth && row.HorizontalOffset >= row.ScrollableWidth) return;
        row.ScrollToHorizontalOffset(Math.Clamp(target, 0, row.ScrollableWidth)); e.Handled = true;
    }
    private async void ToggleDetailsClick(object sender, RoutedEventArgs e)
    {
        if (model.Selected is null && model.Hero is { } hero) await model.Select(hero);
        DetailsPanel.Visibility = DetailsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (DetailsPanel.Visibility == Visibility.Visible) DetailsPanel.BringIntoView();
    }
    private void CloseDetailsClick(object sender, RoutedEventArgs e)
    {
        DetailsPanel.Visibility = Visibility.Collapsed;
        if (model.IsHome) model.ClearSelection();
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
    private async Task ShowPlayer(Movie movie)
    {
        ClosePlayer();
        ++playbackGeneration;
        Sidebar.Visibility = LibraryPanel.Visibility = Visibility.Hidden;
        PlayerHost.Visibility = Visibility.Visible;
        var player = new PlayerView(movie, model.Store);
        activePlayer = player;
        player.CloseRequested += ClosePlayer;
        player.FullscreenRequested += TogglePlayerFullscreen;
        player.DownloadRequested += async selected => await ShowDownloads(selected);
        player.EscapeRequested += () => { if (playerFullscreen) TogglePlayerFullscreen(); else ClosePlayer(); };
        PlayerHost.Child = player; player.Focus();
        // A title from "My library" plays its downloaded file offline; otherwise it streams.
        string? local = movie.LocalFile ?? downloads.Library.FirstOrDefault(i => i.Spec.Movie.Id == movie.Id)?.Destination;
        if (local is not null && File.Exists(local)) player.OpenLocal(movie, local);
        else await player.Open(movie);
    }
    private void TogglePlayerFullscreen()
    {
        if (activePlayer is null) return;
        if (!playerFullscreen) { stateBeforeFullscreen = WindowState; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        else { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = stateBeforeFullscreen; }
        playerFullscreen = !playerFullscreen;
    }
    private async Task ShowDownloads(Movie? movie)
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
