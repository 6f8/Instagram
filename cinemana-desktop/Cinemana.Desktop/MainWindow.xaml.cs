using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Cinemana.Desktop.Models;
using Cinemana.Desktop.Services;
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
            return new(new LibraryStore(Path.Combine(Path.GetTempPath(), "CinemanaPreview", Guid.NewGuid().ToString("N"))));
        return new();
    }
    private PlayerView? activePlayer;
    private bool playerFullscreen, pip;
    private Rect boundsBeforePip;
    private WindowState stateBeforeFullscreen;
    private int playbackGeneration;
    private readonly DownloadManager downloads;
    private DownloadsWindow? downloadsWindow;
    private readonly DispatcherTimer carousel = new() { Interval = TimeSpan.FromSeconds(8) };
    private System.Windows.Forms.NotifyIcon? tray;
    private readonly FollowWatcher follow;
    private readonly DispatcherTimer followTimer = new() { Interval = TimeSpan.FromHours(6) };
    public static readonly DependencyProperty HeroTextWidthProperty = DependencyProperty.Register(nameof(HeroTextWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(560d));
    public double HeroTextWidth { get => (double)GetValue(HeroTextWidthProperty); set => SetValue(HeroTextWidthProperty, value); }
    private const string Shortcuts = """
        /  أو  Ctrl+F      البحث
        D                  التنزيلات
        Esc  أو  Alt+←     رجوع
        F5  أو  Ctrl+R     تحديث
        ?                  هذه القائمة

        في المشغّل:
        Space              تشغيل / إيقاف
        ←  →               10 ثوانٍ للخلف / للأمام
        [  ]               تقديم / تأخير الترجمة
        M                  كتم الصوت
        F11                ملء الشاشة
        """;
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
        model.LibraryProvider = LibraryItems;
        follow = new FollowWatcher(model.Store, downloads.Add, downloads.FolderFor);
        model.IsFollowed = follow.IsFollowed;
        model.FollowToggled += (series, episodes) => follow.Toggle(series, episodes);
        followTimer.Tick += async (_, _) => await CheckFollowed();
        followTimer.Start();
        downloads.QueueFinished += AfterQueue;
        // Taskbar button shows overall download progress.
        TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();
        downloads.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(DownloadManager.TotalsLabel) or nameof(DownloadManager.DockItem)) UpdateTaskbar(); };
        model.DownloadRequested += async movie => await ShowDownloads(movie);
        model.UnwatchedDownloadRequested += async (series, episodes) => await ShowDownloads(series, episodes);
        model.SelectionRequested += () => { CatalogScroll.ScrollToTop(); DetailsPanel.BringIntoView(); };
        Width = Math.Min(1500, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(940, SystemParameters.WorkArea.Height - 32);
        model.WatchRequested += async movie => await ShowPlayer(movie);
        // The banner advances on its own while the home page is shown and nothing is selected.
        carousel.Tick += (_, _) => { if (model.IsHome && PlayerHost.Visibility != Visibility.Visible && IsActive) model.MoveHero(1); };
        carousel.Start();
        Loaded += async (_, _) =>
        {
            await model.Load();
            _ = model.CheckServiceAndUpdates();
            _ = CheckFollowed();
            var args = Environment.GetCommandLineArgs();
            int playIndex = Array.IndexOf(args, "--play");
            if (playIndex < 0 || playIndex + 1 >= args.Length || !args[playIndex + 1].All(char.IsAsciiDigit)) return;
            try
            {
                using var api = new CinemanaService(model.Store.Root);
                var movie = await api.Details(args[playIndex + 1], CancellationToken.None);
                if (IsLoaded) model.WatchCommand.Execute(movie);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
            { if (IsLoaded) MessageBox.Show(this, "تعذّر فتح العنوان المطلوب. يمكنك اختياره من المكتبة.", "سينمانا"); }
        };
        Closed += (_, _) => { playbackGeneration++; carousel.Stop(); followTimer.Stop(); activePlayer?.Dispose(); downloads.Dispose(); model.Dispose(); tray?.Dispose(); Log.Info("closed"); };
        PreviewKeyDown += async (_, e) =>
        {
            if (PlayerHost.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.F11) { TogglePlayerFullscreen(); e.Handled = true; }
                else if (e.Key == Key.Escape) { if (pip) TogglePip(); else if (playerFullscreen) TogglePlayerFullscreen(); else ClosePlayer(); e.Handled = true; }
                else if (activePlayer is not null) e.Handled = activePlayer.HandleKey(e);
                return;
            }
            bool typing = Keyboard.FocusedElement is TextBox;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.F && Keyboard.Modifiers == ModifierKeys.Control || !typing && key is Key.Oem2 or Key.Divide && Keyboard.Modifiers == ModifierKeys.None)
            { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
            else if (key == Key.Escape || key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt || key == Key.BrowserBack)
            {
                if (model.SuggestionsOpen) model.SuggestionsOpen = false;
                else if (typing && SearchBox.Text.Length > 0 && key == Key.Escape) model.SearchText = "";
                else await model.Back();
                e.Handled = true;
            }
            else if (!typing && key == Key.D && Keyboard.Modifiers == ModifierKeys.None) { await ShowDownloads(null); e.Handled = true; }
            else if (!typing && (key == Key.Oem2 && Keyboard.Modifiers == ModifierKeys.Shift || key == Key.F1)) { ShortcutsClick(this, new()); e.Handled = true; }
        };
        MouseDown += async (_, e) => { if (e.ChangedButton == MouseButton.XButton1) { await model.Back(); e.Handled = true; } };
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Completed downloads for "My library", with size and watched state on each card.</summary>
    private IEnumerable<Movie> LibraryItems()
    {
        var watched = model.Store.History().DistinctBy(h => h.Movie.Id).ToDictionary(h => h.Movie.Id, h => h.Percent);
        return downloads.Library
            .GroupBy(i => i.Destination, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(i =>
            {
                string state = watched.TryGetValue(i.Spec.Movie.Id, out var p) ? p >= 95 ? " · شوهدت" : $" · {p:0}%" : "";
                string episode = i.Spec.Movie.Kind == "2" ? $"S{i.Spec.Movie.Season:00}E{i.Spec.Movie.Episode:00} · " : "";
                return i.Spec.Movie with { LocalFile = i.Destination, ProgressText = episode + DownloadPaths.Size(i.FileSize) + state };
            });
    }
    private void ShowCompleted(DownloadItem item)
    {
        if (!model.NotifyOnComplete) return;
        try
        {
            tray ??= new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Text = "سينمانا", Visible = true };
            tray.BalloonTipClicked -= TrayClicked; tray.BalloonTipClicked += TrayClicked;
            tray.ShowBalloonTip(5000, "اكتمل التنزيل", item.Title, System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception e) when (e is ArgumentException or IOException or System.ComponentModel.Win32Exception) { Log.Error("tray", e); }
    }
    private async void TrayClicked(object? sender, EventArgs e) { Activate(); await ShowDownloads(null); }
    private void Balloon(string title, string text)
    {
        try
        {
            tray ??= new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Text = "سينمانا", Visible = true };
            tray.ShowBalloonTip(6000, title, text, System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception e) when (e is ArgumentException or IOException or System.ComponentModel.Win32Exception) { Log.Error("tray", e); }
    }
    private async Task CheckFollowed()
    {
        var news = await follow.Check(model.AutoDownloadFollowed, CancellationToken.None);
        if (news.Count > 0) Balloon("حلقات جديدة", string.Join("\n", news.Take(4)) + (model.AutoDownloadFollowed ? "\nأُضيفت إلى قائمة التنزيل." : ""));
    }
    private void UpdateTaskbar()
    {
        var running = downloads.Items.Where(i => i.State == DownloadState.Running).ToList();
        if (TaskbarItemInfo is null) return;
        if (running.Count == 0) { TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None; return; }
        TaskbarItemInfo.ProgressState = running.All(i => i.Total is null) ? System.Windows.Shell.TaskbarItemProgressState.Indeterminate : System.Windows.Shell.TaskbarItemProgressState.Normal;
        TaskbarItemInfo.ProgressValue = running.Average(i => i.Percent) / 100;
    }
    /// <summary>After the whole queue: sleep or shut down, with one minute to cancel.</summary>
    private void AfterQueue()
    {
        int action = model.AfterQueue;
        if (action == 0) return;
        string what = action == 1 ? "إسبات الجهاز" : "إطفاء الجهاز";
        Log.Info("queue finished, " + what);
        var cancel = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        cancel.Tick += (_, _) =>
        {
            cancel.Stop();
            try
            {
                if (action == 2) Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                else System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("after-queue", e); }
        };
        cancel.Start();
        if (MessageBox.Show(this, $"اكتملت قائمة التنزيل. سيتم {what} خلال دقيقة.\nاضغط «إلغاء» للتراجع.", "سينمانا", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.Cancel)
            cancel.Stop();
    }
    private void RowWheel(object sender, MouseWheelEventArgs e)
    {
        // Rows scroll sideways with the wheel; the page keeps scrolling once a row reaches its end.
        if (sender is not ScrollViewer row || row.ScrollableWidth <= 0) return;
        double target = row.HorizontalOffset - e.Delta;
        if (target < 0 && row.HorizontalOffset <= 0 || target > row.ScrollableWidth && row.HorizontalOffset >= row.ScrollableWidth) return;
        row.ScrollToHorizontalOffset(Math.Clamp(target, 0, row.ScrollableWidth)); e.Handled = true;
    }
    private void SearchFocused(object sender, KeyboardFocusChangedEventArgs e) => model.ShowRecent();
    private async void ToggleDetailsClick(object sender, RoutedEventArgs e)
    {
        if (model.Selected is null && model.Hero is { } hero) await model.Select(hero);
        model.DetailsOpen = !model.DetailsOpen;
        if (model.DetailsOpen) DetailsPanel.BringIntoView();
    }
    private async void OpenDownloadsClick(object sender, RoutedEventArgs e) => await ShowDownloads(null);
    private void OpenUpdateClick(object sender, RoutedEventArgs e) => Open(model.UpdateUrl.Length > 0 ? model.UpdateUrl : UpdateCheck.ReleasesPage);
    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("open", e); ToolStatus.Text = "تعذّر الفتح."; }
    }
    // --- Settings tools ---
    private void CopyReportClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Log.Report()); ToolStatus.Text = "نُسخ التقرير. الصقه في رسالتك (Ctrl+V)."; }
        catch (COMException) { ToolStatus.Text = "تعذّر الوصول إلى الحافظة. أعد المحاولة."; }
    }
    private void OpenDataClick(object sender, RoutedEventArgs e) => Open(model.Store.Root);
    private async void RescanClick(object sender, RoutedEventArgs e)
    {
        ToolStatus.Text = "جارٍ فحص مجلد التنزيل…";
        int added = await downloads.Rescan();
        ToolStatus.Text = added > 0 ? $"أُضيف {added} ملفًا إلى مكتبتي." : "لا توجد ملفات جديدة. كل الملفات المكتملة موجودة في مكتبتي.";
        if (model.Section == "library") await model.Load();
    }
    private void CleanupClick(object sender, RoutedEventArgs e)
    {
        var stale = downloads.StalePartials();
        if (stale.Count == 0) { ToolStatus.Text = "لا توجد ملفات جزئية قديمة."; return; }
        long size = stale.Sum(f => f.Length);
        if (MessageBox.Show(this, $"يوجد {stale.Count} ملفًا جزئيًا قديمًا ({DownloadPaths.Size(size)}) لا يتبع أي تنزيل في القائمة. حذفها؟", "تنظيف", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ToolStatus.Text = $"تم تحرير {DownloadPaths.Size(downloads.DeleteFiles(stale))}.";
    }
    private void ClearCacheClick(object sender, RoutedEventArgs e)
    {
        string cache = Path.Combine(model.Store.Root, "Cache");
        try
        {
            var files = Directory.Exists(cache) ? new DirectoryInfo(cache).GetFiles("*.img") : [];
            long size = files.Sum(f => f.Length);
            if (MessageBox.Show(this, $"ذاكرة الصور تستخدم {DownloadPaths.Size(size)}. مسحها؟ ستُحمَّل الصور من جديد عند الحاجة.", "مسح ذاكرة الصور", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            foreach (var f in files) { try { f.Delete(); } catch (IOException) { } }
            ToolStatus.Text = $"تم تحرير {DownloadPaths.Size(size)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("cache", ex); ToolStatus.Text = "تعذّر مسح ذاكرة الصور."; }
    }
    private void ShortcutsClick(object sender, RoutedEventArgs e) => MessageBox.Show(this, Shortcuts, "اختصارات لوحة المفاتيح");
    private async void CheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        ToolStatus.Text = "جارٍ التحقق…";
        await model.CheckServiceAndUpdates();
        ToolStatus.Text = model.HasUpdate ? model.UpdateNotice : $"لديك أحدث إصدار ({Log.Version}).";
    }
    private async void DeleteLocalClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Movie { LocalFile: { } file } movie }) return;
        var item = downloads.Items.FirstOrDefault(i => string.Equals(i.Destination, file, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        if (MessageBox.Show(this, $"حذف «{movie.DisplayTitle}» ({DownloadPaths.Size(item.FileSize)}) وملف ترجمته من القرص؟", "حذف من مكتبتي", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (downloads.DeleteFile(item)) await model.Load();
    }
    // --- Player and downloads ---
    private void ClosePlayer()
    {
        playbackGeneration++;
        if (playerFullscreen) TogglePlayerFullscreen();
        if (pip) TogglePip();
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
        player.PipRequested += TogglePip;
        player.DownloadRequested += async selected => await ShowDownloads(selected);
        player.EscapeRequested += () => { if (playerFullscreen) TogglePlayerFullscreen(); else ClosePlayer(); };
        PlayerHost.Child = player; player.Focus();
        // A title from "My library" plays its downloaded file offline; otherwise it streams.
        string? local = movie.LocalFile ?? downloads.Library.FirstOrDefault(i => i.Spec.Movie.Id == movie.Id)?.Destination;
        Log.Info(local is not null && File.Exists(local) ? "play local" : "play stream");
        if (local is not null && File.Exists(local)) player.OpenLocal(movie, local);
        else await player.Open(movie);
    }
    /// <summary>Picture-in-picture: a small borderless window that stays above other apps; P or Esc returns.</summary>
    private void TogglePip()
    {
        if (activePlayer is null) return;
        if (!pip)
        {
            if (playerFullscreen) TogglePlayerFullscreen();
            boundsBeforePip = new Rect(Left, Top, Width, Height);
            WindowState = WindowState.Normal; WindowStyle = WindowStyle.ToolWindow; Topmost = true; ResizeMode = ResizeMode.CanResizeWithGrip;
            Width = 480; Height = 300; Left = SystemParameters.WorkArea.Right - Width - 16; Top = SystemParameters.WorkArea.Bottom - Height - 16;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow; Topmost = false; ResizeMode = ResizeMode.CanResize;
            Left = boundsBeforePip.Left; Top = boundsBeforePip.Top; Width = boundsBeforePip.Width; Height = boundsBeforePip.Height;
        }
        pip = !pip; activePlayer.SetCompact(pip);
    }
    private void TogglePlayerFullscreen()
    {
        if (activePlayer is null) return;
        if (!playerFullscreen) { stateBeforeFullscreen = WindowState; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        else { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = stateBeforeFullscreen; }
        playerFullscreen = !playerFullscreen;
    }
    private async Task ShowDownloads(Movie? movie, List<Movie>? only = null)
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
            await downloadsWindow.SetTarget(movie, season, only);
        }
    }
}
