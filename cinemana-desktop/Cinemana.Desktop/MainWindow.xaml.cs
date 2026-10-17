using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        model.SubscriptionsProvider = () => follow.All().Select(f => new Movie(f.Id, f.Title, "", f.Poster, "", "") { ArabicTitle = f.DisplayTitle, Kind = "2", ProgressText = $"{f.Known.Count} حلقة" });
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
        undoTimer.Tick += (_, _) => { CommitPendingDelete(); _ = model.Load(); };
        ApplyScale();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.UiScale)) { ApplyScale(); AdaptLayout(); }
            if (e.PropertyName == nameof(MainViewModel.Hero)) FadeBanner();
            if (e.PropertyName == nameof(MainViewModel.DetailsOpen) && model.DetailsOpen) FadeIn(DetailsPanel);
        };
        model.StartupChanged += ApplyStartup;
        model.TvModeChanged += on => { if (on && WindowState != WindowState.Maximized) { WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; } else if (!on) { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = WindowState.Normal; } };
        SizeChanged += (_, _) => AdaptLayout();
        // Ctrl + mouse wheel zooms the whole interface.
        PreviewMouseWheel += (_, e) => { if (Keyboard.Modifiers == ModifierKeys.Control && PlayerHost.Visibility != Visibility.Visible) { model.UiScale += e.Delta > 0 ? 0.05 : -0.05; e.Handled = true; } };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) { if (model.StartMinimized) MinimizeToTray(); TrimMemory(); } };
        Width = Math.Min(1500, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(940, SystemParameters.WorkArea.Height - 32);
        model.WatchRequested += async movie => await ShowPlayer(movie);
        // The banner advances on its own while the home page is shown and nothing is selected.
        carousel.Tick += (_, _) => { if (model.IsHome && PlayerHost.Visibility != Visibility.Visible && IsActive) model.MoveHero(1); };
        carousel.Start();
        Loaded += async (_, _) =>
        {
            var started = Stopwatch.StartNew();
            if (Environment.GetCommandLineArgs().Contains("--minimized")) MinimizeToTray();
            if (App.SafeMode) model.SetStatus("الوضع الآمن: بدأ التطبيق بالإعدادات الافتراضية. حُفظت إعداداتك السابقة باسم settings.before-safe-mode.json.");
            if (!model.OnboardingDone) ShowTour();
            if (model.MenuCollapsed) SetMenuCollapsed(true);
            await model.Load();
            Log.Info($"startup: first page shown in {started.ElapsedMilliseconds} ms");
            _ = model.CheckServiceAndUpdates();
            _ = CheckFollowed();
            _ = Task.Run(() => CinemanaService.TrimCache(model.Store.Root, 300L << 20));
            if (model.SyncFolder.Length > 0) { try { if (Backup.Sync(model.Store.Root, model.SyncFolder).Down > 0) await model.Load(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("sync", ex); } }
            if (model.AutoDeleteDays > 0)
            {
                int removed = downloads.DeleteWatchedOlderThan(model.AutoDeleteDays, model.Store.History().DistinctBy(h => h.Movie.Id).ToDictionary(h => h.Movie.Id));
                if (removed > 0) Balloon("تنظيف المكتبة", $"حُذف {removed} ملفًا شوهد قبل أكثر من {model.AutoDeleteDays} يومًا.");
            }
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
        Closed += (_, _) => { if (model.SyncFolder.Length > 0) { try { Backup.Sync(model.Store.Root, model.SyncFolder); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } } CommitPendingDelete(); playbackGeneration++; carousel.Stop(); followTimer.Stop(); activePlayer?.Dispose(); downloads.Dispose(); model.Dispose(); tray?.Dispose(); Log.Info("closed"); };
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
            .Where(i => !hiddenFromLibrary.Contains(i.Destination))
            .GroupBy(i => i.Destination, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Select(i =>
            {
                string state = watched.TryGetValue(i.Spec.Movie.Id, out var p) ? p >= 95 ? " · شوهدت" : $" · {p:0}%" : "";
                string episode = i.Spec.Movie.Kind == "2" ? $"S{i.Spec.Movie.Season:00}E{i.Spec.Movie.Episode:00} · " : "";
                DateTime added = DateTime.MinValue;
                try { added = File.GetCreationTime(i.Destination); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return i.Spec.Movie with
                {
                    LocalFile = i.Destination, ProgressText = episode + DownloadPaths.Size(i.FileSize) + state,
                    AddedAt = added, SizeBytes = i.FileSize, WatchedPercent = watched.GetValueOrDefault(i.Spec.Movie.Id),
                    // Posters saved while online keep "My library" illustrated without a connection.
                    LocalPoster = i.Spec.Movie.LocalPoster ?? CinemanaService.CachedPoster(model.Store.Root, i.Spec.Movie.Poster)
                };
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
        var found = await model.CheckSavedSearches(CancellationToken.None);
        if (found.Count > 0) Balloon("نتائج بحث جديدة", string.Join("\n", found.Take(4)));
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
        // No confirmation dialog: the card disappears now and the file is deleted after 10 seconds unless undone.
        CommitPendingDelete();
        pendingDelete = item; hiddenFromLibrary.Add(file);
        UndoText.Text = $"سيُحذف «{movie.DisplayTitle}» ({DownloadPaths.Size(item.FileSize)}) خلال 10 ثوانٍ.";
        UndoBar.Visibility = Visibility.Visible;
        undoTimer.Stop(); undoTimer.Start();
        await model.Load();
    }
    private DownloadItem? pendingDelete;
    private readonly HashSet<string> hiddenFromLibrary = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer undoTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private void CommitPendingDelete()
    {
        undoTimer.Stop(); UndoBar.Visibility = Visibility.Collapsed;
        if (pendingDelete is { } item) { pendingDelete = null; downloads.DeleteFile(item); hiddenFromLibrary.Remove(item.Destination); }
    }
    private async void UndoClick(object sender, RoutedEventArgs e)
    {
        undoTimer.Stop(); UndoBar.Visibility = Visibility.Collapsed;
        if (pendingDelete is { } item) { hiddenFromLibrary.Remove(item.Destination); pendingDelete = null; await model.Load(); }
    }
    // --- Look: scale, adaptive layout, transitions ---
    private void ApplyScale()
    {
        double scale = model.UiScale;
        RootLayout.LayoutTransform = Math.Abs(scale - 1) < 0.01 ? Transform.Identity : new ScaleTransform(scale, scale);
    }
    private void AdaptLayout()
    {
        double width = ActualWidth / Math.Max(0.5, model.UiScale);
        // Narrow windows: the banner poster panel steps aside so the title keeps its space.
        bool narrow = width < 1180;
        if (Banner.ColumnDefinitions.Count == 3) { Banner.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(5, GridUnitType.Star); Banner.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 24); }
        model.SetAvailableWidth(width - 112 - 20);
    }
    private void FadeBanner()
    {
        Banner.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase() });
    }
    private void FadeIn(UIElement element) => element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
    private void RestartClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true }); Application.Current.Shutdown(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("restart", ex); ToolStatus.Text = "أغلق التطبيق وافتحه يدويًا لتطبيق التغييرات."; }
    }
    /// <summary>Registers or removes the app in the current user's Windows start-up list (no admin rights).</summary>
    private void ApplyStartup()
    {
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (model.StartWithWindows) run.SetValue("Cinemana", $"\"{Environment.ProcessPath}\"" + (model.StartMinimized ? " --minimized" : ""));
            else run.DeleteValue("Cinemana", false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { Log.Error("startup", e); }
    }
    private void MinimizeToTray()
    {
        try
        {
            tray ??= new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Text = "سينمانا", Visible = true };
            tray.Visible = true;
            tray.DoubleClick -= RestoreFromTray; tray.DoubleClick += RestoreFromTray;
            Hide();
        }
        catch (Exception e) when (e is ArgumentException or IOException or System.ComponentModel.Win32Exception) { Log.Error("tray", e); }
    }
    private void RestoreFromTray(object? sender, EventArgs e) { Show(); WindowState = WindowState.Normal; Activate(); }
    // --- First-run tour ---
    private int tourStep;
    private static readonly (string Title, string Text)[] TourPages =
    [
        ("أهلًا بك في سينمانا", "تصفّح الأفلام والمسلسلات من الشريط العلوي، وابحث بالضغط على / . اضغط أي بطاقة لفتح صفحتها وحلقاتها."),
        ("التحميل بضغطة", "من صفحة المسلسل اختر «تحميل» فتفتح نافذة جاهزة: الموسم الحالي، أفضل جودة، وترجمة عربية .srt بجانب كل حلقة. يمكنك متابعة المسلسل ليُحمَّل الجديد تلقائيًا."),
        ("مكتبتي والمشاهدة بدون إنترنت", "كل ما حمّلته يظهر في «مكتبتي» مرتبًا حسب المسلسل والموسم، ويُشغَّل مع ترجمته بدون اتصال. الاختصارات كلها في الإعدادات أو بالضغط على ?.")
    ];
    private void ShowTour() { tourStep = 0; ShowTourPage(); Tour.Visibility = Visibility.Visible; FadeIn(Tour); }
    private void ShowTourPage()
    {
        TourStep.Text = $"{tourStep + 1} / {TourPages.Length}"; TourTitle.Text = TourPages[tourStep].Title; TourText.Text = TourPages[tourStep].Text;
        TourNext.Content = tourStep == TourPages.Length - 1 ? "ابدأ" : "التالي";
    }
    private void TourNextClick(object sender, RoutedEventArgs e) { if (++tourStep >= TourPages.Length) TourSkipClick(sender, e); else ShowTourPage(); }
    private void TourSkipClick(object sender, RoutedEventArgs e) { Tour.Visibility = Visibility.Collapsed; model.OnboardingDone = true; }
    private void TourClick(object sender, RoutedEventArgs e) => ShowTour();
    // --- Updates, backup, sync, about ---
    private async void InstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (model.PendingUpdate is not { } update) return;
        if (downloads.Items.Any(i => i.State == DownloadState.Running) &&
            MessageBox.Show(this, "التنزيلات الجارية ستتوقف وتُستأنف بعد التحديث. المتابعة؟", "تحديث", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var progress = new Progress<int>(p => model.SetStatus($"جارٍ تنزيل التحديث… {p}%"));
        if (await UpdateCheck.Install(update, progress, CancellationToken.None)) Application.Current.Shutdown();
        else { model.SetStatus("تعذّر التحديث التلقائي. افتح صفحة الإصدار وحمّل Cinemana.exe يدويًا."); OpenUpdateClick(sender, e); }
    }
    private void BackupClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "حفظ نسخة احتياطية", Filter = "نسخة سينمانا (*.zip)|*.zip", FileName = $"cinemana-backup-{DateTime.Now:yyyy-MM-dd}.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try { Backup.Create(model.Store.Root, dialog.FileName); ToolStatus.Text = "حُفظت النسخة الاحتياطية."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("backup", ex); ToolStatus.Text = "تعذّر حفظ النسخة الاحتياطية."; }
    }
    private void RestoreClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "استعادة نسخة احتياطية", Filter = "نسخة سينمانا (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "ستُستبدل المفضلة والسجل والإعدادات الحالية (تُحفظ نسخة منها). ثم يُعاد تشغيل التطبيق. المتابعة؟", "استعادة", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { downloads.Dispose(); Backup.Restore(model.Store.Root, dialog.FileName); RestartClick(sender, e); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Log.Error("restore", ex); ToolStatus.Text = "الملف ليس نسخة احتياطية صالحة."; }
    }
    private void ChooseSyncClick(object sender, RoutedEventArgs e)
    {
        var chooser = new Microsoft.Win32.OpenFolderDialog { Title = "مجلد المزامنة (مثل OneDrive\\Cinemana)" };
        if (chooser.ShowDialog(this) == true) { model.SyncFolder = chooser.FolderName; SyncNowClick(sender, e); }
    }
    private void SyncNowClick(object sender, RoutedEventArgs e)
    {
        if (model.SyncFolder.Length == 0) { ToolStatus.Text = "اختر مجلد المزامنة أولًا."; return; }
        try { var (up, down) = Backup.Sync(model.Store.Root, model.SyncFolder); ToolStatus.Text = $"تمت المزامنة: أُرسل {up} وجُلب {down}."; if (down > 0) _ = model.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("sync", ex); ToolStatus.Text = "تعذّر الوصول إلى مجلد المزامنة."; }
    }
    private void ChangelogClick(object sender, RoutedEventArgs e) => Open(UpdateCheck.ReleasesPage);
    private void SaveReportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "حفظ تقرير المشكلة", Filter = "ملف نصي (*.txt)|*.txt", FileName = "cinemana-report.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, Log.Report(), new System.Text.UTF8Encoding(true)); ToolStatus.Text = "حُفظ التقرير. أرفقه في رسالتك."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ToolStatus.Text = "تعذّر حفظ الملف."; }
    }
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
    /// <summary>When minimised, give memory back to Windows (images and caches are rebuilt on demand).</summary>
    private static void TrimMemory()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or EntryPointNotFoundException) { }
    }
    // --- Side menu ---
    /// <summary>☰ folds the side menu to its icons (as on the site) and back; the choice is remembered.</summary>
    private void MenuToggleClick(object sender, RoutedEventArgs e) => SetMenuCollapsed(SideMenu.Width > 100);
    private void SetMenuCollapsed(bool collapsed)
    {
        SideMenu.Width = collapsed ? 74 : 270;
        MenuTitle.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var label in Descendants(SideMenu).OfType<TextBlock>().Where(t => t.Style == (Style)FindResource("SideLabel")))
            label.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        model.MenuCollapsed = collapsed;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private void GenreMenuClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string genre }) model.NavigateCommand.Execute("genre:" + genre); }
    private void GridCardClick(object sender, MouseButtonEventArgs e)
    {
        // A click on the poster (not on its buttons) opens the title page.
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) is not null) return;
        if (sender is FrameworkElement { DataContext: Movie m }) model.SelectCommand.Execute(m);
    }
    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var node = d; node is not null; node = VisualTreeHelper.GetParent(node)) if (node is T hit) return hit;
        return null;
    }
    private void CardLaterClick(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: Movie m }) model.AddToList("سأشاهده لاحقًا", m); }
    private void HeroLaterClick(object sender, RoutedEventArgs e) { if (model.Hero is { } m) model.AddToList("سأشاهده لاحقًا", m); }
    // --- Discovery ---
    private async void RandomClick(object sender, RoutedEventArgs e) => await model.PickRandom();
    private async void CastClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name }) { model.SearchText = name; await Task.Delay(1); model.SearchCommand.Execute(null); }
    }
    private void SaveSearchClick(object sender, RoutedEventArgs e) => model.SaveCurrentSearch();
    private void ShowHiddenClick(object sender, RoutedEventArgs e) { model.ClearHidden(); ToolStatus.Text = "عادت العناوين المخفية للظهور."; }
    /// <summary>Right-click on a card: watch, download, favourite list, copy title, hide.</summary>
    private void CardMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Movie movie, ContextMenu: { } menu }) return;
        menu.FlowDirection = FlowDirection.RightToLeft; menu.Items.Clear();
        void Add(string header, Action action) { var item = new MenuItem { Header = header }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("مشاهدة", () => model.WatchCommand.Execute(movie));
        if (movie.IsRemote) Add("تحميل…", () => model.DownloadCommand.Execute(movie));
        Add("التفاصيل والحلقات", () => model.SelectCommand.Execute(movie));
        Add("أضف إلى «سأشاهده لاحقًا»", () => model.AddToList("سأشاهده لاحقًا", movie));
        Add("نسخ الاسم", () => { try { Clipboard.SetText(movie.DisplayTitle); } catch (COMException) { } });
        menu.Items.Add(new Separator());
        Add("إخفاء من الاقتراحات", () => model.Hide(movie));
    }
    // --- My library tools ---
    private async void ImportVideosClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "اختر ملفات فيديو لإضافتها إلى مكتبتي", Filter = "فيديو (*.mp4;*.mkv;*.ts)|*.mp4;*.mkv;*.ts", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        int added = downloads.ImportFiles(dialog.FileNames);
        model.SetStatus(added > 0 ? $"أُضيف {added} ملفًا إلى مكتبتي." : "هذه الملفات موجودة بالفعل في مكتبتي.");
        await model.Load();
    }
    private void ExportCsvClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "تصدير مكتبتي", Filter = "CSV (*.csv)|*.csv", FileName = "مكتبتي.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, model.LibraryCsv(), new System.Text.UTF8Encoding(true)); model.SetStatus("صُدّرت المكتبة."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { model.SetStatus("تعذّر حفظ الملف."); }
    }
    private async void MoveLibraryClick(object sender, RoutedEventArgs e)
    {
        var chooser = new Microsoft.Win32.OpenFolderDialog { Title = "اختر المجلد الجديد للمكتبة" };
        if (chooser.ShowDialog(this) != true) return;
        if (downloads.Items.Any(i => i.State == DownloadState.Running)) { MessageBox.Show(this, "أوقف التنزيلات الجارية أولًا ثم أعد المحاولة.", "نقل المكتبة"); return; }
        var progress = new Progress<int>(n => model.SetStatus($"جارٍ النقل… {n} ملف"));
        var (moved, failed) = await downloads.MoveLibrary(chooser.FolderName, progress);
        model.SetStatus($"نُقل {moved} ملفًا" + (failed > 0 ? $"، وتعذّر نقل {failed}." : ".") + " صار المجلد الجديد مجلد التنزيل.");
        await model.Load();
    }
    private void AddToListClick(object sender, RoutedEventArgs e)
    {
        if (model.Selected is not { } movie || sender is not FrameworkElement button) return;
        var menu = new ContextMenu { FlowDirection = FlowDirection.RightToLeft, PlacementTarget = button };
        foreach (var name in model.Lists().Keys.Append("سأشاهده لاحقًا").Distinct().Order())
        {
            var item = new MenuItem { Header = name }; item.Click += (_, _) => model.AddToList(name, movie); menu.Items.Add(item);
        }
        var create = new MenuItem { Header = "قائمة جديدة…" };
        create.Click += (_, _) => { if (Prompt("اسم القائمة الجديدة") is { Length: > 0 } name) model.AddToList(name, movie); };
        menu.Items.Add(new Separator()); menu.Items.Add(create);
        if (model.IsFavorites && model.ActiveList.Length > 0)
        { var remove = new MenuItem { Header = "إزالة من «" + model.ActiveList + "»" }; remove.Click += (_, _) => model.RemoveFromActiveList(movie); menu.Items.Add(remove); }
        menu.IsOpen = true;
    }
    /// <summary>A one-line text question in the app's style; null when cancelled.</summary>
    private string? Prompt(string question)
    {
        var box = new TextBox { Margin = new Thickness(0, 10, 0, 14), MinWidth = 280 };
        var ok = new Button { Content = "موافق", IsDefault = true, Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(20, 8, 20, 8) };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = question }); panel.Children.Add(box); panel.Children.Add(ok);
        var dialog = new Window { Title = "سينمانا", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FlowDirection = FlowDirection.RightToLeft, Background = (System.Windows.Media.Brush)FindResource("Ground"), Foreground = (System.Windows.Media.Brush)FindResource("Ink"), ResizeMode = ResizeMode.NoResize };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => box.Focus();
        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }
    // --- Player and downloads ---
    private void ClosePlayer()
    {
        playbackGeneration++;
        if (playerFullscreen) TogglePlayerFullscreen();
        if (pip) TogglePip();
        activePlayer?.Dispose(); activePlayer = null;
        PlayerHost.Child = null; PlayerHost.Visibility = Visibility.Collapsed;
        Sidebar.Visibility = LibraryPanel.Visibility = SideMenu.Visibility = Visibility.Visible;
        model.RefreshHistory();
    }
    private async Task ShowPlayer(Movie movie)
    {
        ClosePlayer();
        ++playbackGeneration;
        Sidebar.Visibility = LibraryPanel.Visibility = SideMenu.Visibility = Visibility.Hidden;
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
