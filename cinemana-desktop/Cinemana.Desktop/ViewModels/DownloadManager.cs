using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Cinemana.Desktop.Models;
using Cinemana.Desktop.Services;

namespace Cinemana.Desktop.ViewModels;

public sealed class DownloadItem : Observable
{
    public DownloadSpec Spec { get; }
    private DownloadState state;
    private long bytes;
    private long? total;
    private double speed;
    private string message;
    public DownloadItem(DownloadSnapshot value)
    { Spec = value.Spec; state = value.State; bytes = value.Bytes; total = value.Total; message = value.Message; }
    public string Title => Spec.Movie.DisplayTitle + (Spec.Movie.Kind == "2" ? " • " + Spec.Movie.EpisodeLabel : "");
    public string Poster => Spec.Movie.ImageSource;
    public string QualityLabel => Spec.Quality + "p" + (Spec.SubtitleLanguage.Length > 0 ? " + SRT" : "");
    public string QualityChip => Spec.Quality + "p";
    public string Destination => Spec.Destination;
    public DownloadState State { get => state; set { Set(ref state, value); Refresh(); } }
    public string Message { get => message; set => Set(ref message, value); }
    public string StatusLabel => State switch { DownloadState.Queued => "في الانتظار", DownloadState.Running => "جارٍ التنزيل …", DownloadState.Paused => "متوقف مؤقتًا", DownloadState.Completed => "مكتمل", DownloadState.Failed => "تعذّر التنزيل", DownloadState.Cancelled => "ملغي", _ => "الجودة غير متاحة" };
    public double Percent => total > 0 ? Math.Clamp(bytes * 100d / total.Value, 0, 100) : 0;
    public bool Indeterminate => State == DownloadState.Running && total is null;
    public long Bytes => bytes;
    public long? Total => total;
    public double Speed => State == DownloadState.Running ? speed : 0;
    public long Remaining => total is { } t ? Math.Max(0, t - bytes) : 0;
    // "4.2 GB / 8.5 GB (49%)" as in the design's download bar.
    public string SizeLabel => DownloadPaths.Size(bytes) + (total.HasValue ? $" / {DownloadPaths.Size(total.Value)} ({Percent:0}%)" : "");
    public string SpeedLabel => Speed > 0 ? DownloadPaths.Size((long)Speed) + "/s" : "";
    public string EtaLabel => Speed > 0 && total.HasValue ? "متبقٍ " + DownloadManager.Duration(Remaining / Speed) : "";
    public string RateLine => string.Join(" • ", new[] { SpeedLabel, EtaLabel }.Where(x => x.Length > 0));
    public string ProgressLabel => SizeLabel + (RateLine.Length > 0 ? " • " + RateLine : "");
    public bool CanPause => State is DownloadState.Running or DownloadState.Queued;
    public bool CanResume => State is DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled or DownloadState.Unavailable;
    public bool CanCancel => State is not (DownloadState.Completed or DownloadState.Cancelled);
    public bool IsPlayable => State == DownloadState.Completed && File.Exists(Destination);
    public long FileSize => total ?? bytes;
    public void Progress(DownloadProgress value) { bytes = value.Bytes; total = value.Total; speed = value.BytesPerSecond; Refresh(); }
    private void Refresh()
    {
        foreach (var name in new[] { nameof(StatusLabel), nameof(Percent), nameof(Indeterminate), nameof(ProgressLabel), nameof(SizeLabel), nameof(SpeedLabel), nameof(EtaLabel), nameof(RateLine), nameof(CanPause), nameof(CanResume), nameof(CanCancel), nameof(IsPlayable) })
            Notify(name);
    }
    public DownloadSnapshot Snapshot() => new(Spec, State, bytes, total, Message);
    /// <summary>Automatic retries after a dropped connection in this session.</summary>
    public int AutoRetries { get; set; }
}

public sealed class DownloadManager : Observable, IDisposable
{
    private readonly LibraryStore store;
    private readonly CinemanaService api;
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly FileDownloader downloader;
    private readonly Dictionary<DownloadItem, CancellationTokenSource> active = [];
    private int workers;
    private bool disposed;
    private DateTimeOffset lastSaved;
    private string directory;
    private string status = "التنزيلات تستمر أثناء استخدام التطبيق.";
    public ObservableCollection<DownloadItem> Items { get; } = [];
    /// <summary>Raised on the UI thread when a file finishes, for the tray notification.</summary>
    public event Action<DownloadItem>? Completed;
    public DownloadManager(LibraryStore store)
    {
        this.store = store; api = new(store.Root);
        downloader = new(http, (movie, token) => api.Playback(movie.Id, token))
        {
            // The overall limit is shared evenly between the transfers running at that moment.
            SpeedLimit = () => Settings.SpeedLimitKBps <= 0 ? 0 : Settings.SpeedLimitKBps * 1024L / Math.Max(1, active.Count)
        };
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (videos.Length == 0) videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
        directory = store.Load("download-directory", Path.Combine(videos, "Cinemana"));
        bool resume = Settings.AutoResumeDownloads;
        foreach (var saved in store.Load("downloads", new List<DownloadSnapshot>()))
        {
            var state = saved.State is DownloadState.Running or DownloadState.Queued ? (resume ? DownloadState.Queued : DownloadState.Paused) : saved.State;
            Items.Add(new(saved with { State = state }));
        }
        PauseCommand = new(p => { if (p is DownloadItem item) Pause(item); });
        ResumeCommand = new(p => { if (p is DownloadItem item && item.CanResume) { item.State = DownloadState.Queued; item.Message = ""; Save(); Start(); } });
        CancelCommand = new(p => { if (p is DownloadItem item) Cancel(item); });
        RemoveCommand = new(p => { if (p is DownloadItem item && !item.CanPause) { Items.Remove(item); Save(); } });
        ToggleCommand = new(p => { if (p is DownloadItem item) { if (item.CanPause) Pause(item); else if (item.CanResume) ResumeCommand.Execute(item); } });
        PauseAllCommand = new(_ => PauseAll()); ResumeAllCommand = new(_ => { foreach (var item in Items.Where(i => i.State == DownloadState.Paused)) item.State = DownloadState.Queued; Save(); Start(); });
        FolderCommand = new(p => { if (p is DownloadItem item) OpenFolder(Path.GetDirectoryName(item.Destination)!); else OpenFolder(Directory); });
        if (resume) Start();
        _ = Rescan();
    }
    /// <summary>Adds finished videos found in the download folder that the queue does not list (after a reinstall or a moved folder).</summary>
    public async Task<int> Rescan()
    {
        var known = Items.Select(i => i.Destination).ToList();
        string root = directory;
        var found = await Task.Run(() => LibraryScanner.Scan(root, known).ToList());
        foreach (var snapshot in found) Items.Add(new(snapshot));
        if (found.Count > 0) { Status = $"أُضيف {found.Count} ملفًا موجودًا إلى مكتبتي."; Save(); }
        return found.Count;
    }
    /// <summary>Deletes a finished file and the subtitles saved beside it, then drops it from the list.</summary>
    public bool DeleteFile(DownloadItem item)
    {
        if (item.CanPause) return false;
        try
        {
            foreach (var srt in FileDownloader.SubtitlesBeside(item.Destination).ToList()) File.Delete(srt);
            foreach (var path in new[] { item.Destination, item.Destination + ".download.json", item.Destination + ".part" }) if (File.Exists(path)) File.Delete(path);
            Items.Remove(item); Save(); Log.Info("deleted a downloaded file");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("delete", e); Status = "تعذّر حذف الملف. ربما هو مفتوح في برنامج آخر."; return false; }
    }
    /// <summary>Partial downloads (.part) older than three days that no unfinished item still owns.</summary>
    public List<FileInfo> StalePartials()
    {
        var owned = new HashSet<string>(Items.Where(i => i.State != DownloadState.Completed).Select(i => Path.GetFullPath(i.Destination) + ".part"), StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!System.IO.Directory.Exists(directory)) return [];
            return System.IO.Directory.EnumerateFiles(directory, "*.part", SearchOption.AllDirectories).Select(f => new FileInfo(f))
                .Where(f => !owned.Contains(f.FullName) && DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromDays(3)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("stale-scan", e); return []; }
    }
    public long DeleteFiles(IEnumerable<FileInfo> files)
    {
        long freed = 0;
        foreach (var f in files)
        {
            try { long size = f.Length; f.Delete(); if (File.Exists(f.FullName[..^5] + ".download.json") && !File.Exists(f.FullName[..^5])) File.Delete(f.FullName[..^5] + ".download.json"); freed += size; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("cleanup", e); }
        }
        return freed;
    }
    /// <summary>Refuses to start when the drive is clearly too small for the file (size from an earlier attempt, if known).</summary>
    private static string? CheckDiskSpace(DownloadItem item)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(item.Destination))!);
            long need = Math.Max(item.Remaining, 200L << 20);
            return drive.AvailableFreeSpace < need ? $"المساحة الحرة في القرص {drive.Name} ({DownloadPaths.Size(drive.AvailableFreeSpace)}) لا تكفي. حرّر مساحة أو اختر مجلدًا آخر ثم اضغط متابعة." : null;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
    public string FreeSpaceLabel
    {
        get
        {
            try { var drive = new DriveInfo(Path.GetPathRoot(directory)!); return $"المساحة الحرة: {DownloadPaths.Size(drive.AvailableFreeSpace)}"; }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return ""; }
        }
    }
    private AppSettings Settings => store.Load("settings", new AppSettings());
    public string Directory { get => directory; set { if (Set(ref directory, Path.GetFullPath(value))) { try { store.Save("download-directory", directory); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "تعذّر حفظ مجلد التنزيل."; } } } }
    public string Status { get => status; private set => Set(ref status, value); }
    public DownloadItem? DockItem => Items.FirstOrDefault(i => i.State == DownloadState.Running) ?? Items.FirstOrDefault(i => i.State == DownloadState.Paused) ?? Items.LastOrDefault();
    public bool HasDockItem => DockItem is not null;
    public int PendingCount => Items.Count(i => i.State is DownloadState.Running or DownloadState.Queued or DownloadState.Paused);
    public bool HasPending => PendingCount > 0;
    public string Summary => $"{Items.Count(i => i.State == DownloadState.Completed)} مكتمل • {Items.Count(i => i.State == DownloadState.Running)} جارٍ • {Items.Count(i => i.State == DownloadState.Queued)} في الانتظار";
    /// <summary>Totals over unfinished files whose size the server has announced; unknown sizes are not guessed.</summary>
    public string TotalsLabel
    {
        get
        {
            var open = Items.Where(i => i.State is DownloadState.Running or DownloadState.Queued or DownloadState.Paused).ToList();
            if (open.Count == 0) return "";
            long remaining = open.Sum(i => i.Remaining);
            double speed = open.Sum(i => i.Speed);
            int unknown = open.Count(i => i.Total is null);
            string text = $"المتبقي {DownloadPaths.Size(remaining)}" + (speed > 0 ? $" • {DownloadPaths.Size((long)speed)}/s • نحو {Duration(remaining / speed)}" : "");
            return unknown > 0 ? text + $" • {unknown} ملف لم يُعرف حجمه بعد" : text;
        }
    }
    private List<DownloadItem> Open => Items.Where(i => i.State is DownloadState.Running or DownloadState.Queued or DownloadState.Paused).ToList();
    public string RemainingLabel => DownloadPaths.Size(Open.Sum(i => i.Remaining));
    public string EtaLabel { get { double speed = Open.Sum(i => i.Speed); return speed > 0 ? Duration(Open.Sum(i => i.Remaining) / speed) : "—"; } }
    public string RunningLabel => $"{Items.Count(i => i.State == DownloadState.Running)} / {Open.Count}"; 
    public IEnumerable<DownloadItem> Library => Items.Where(i => i.IsPlayable);
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand PauseAllCommand { get; }
    public RelayCommand ResumeAllCommand { get; }
    public RelayCommand FolderCommand { get; }
    public static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} س {t.Minutes} د" : t.TotalMinutes >= 1 ? $"{t.Minutes} دقائق" : $"{t.Seconds} ث";
    }
    public int Add(IEnumerable<DownloadSpec> plan)
    {
        int added = 0;
        foreach (var spec in plan)
        {
            if (Items.Any(i => i.Spec.Key == spec.Key && (i.State != DownloadState.Completed || File.Exists(i.Destination)))) continue;
            Items.Add(new(new(spec, DownloadState.Queued))); added++;
        }
        Status = added > 0 ? $"أُضيف {added} ملفًا إلى قائمة التنزيل." : "هذه الملفات موجودة بالفعل في القائمة.";
        Save(); Start(); return added;
    }
    /// <summary>Fills free worker slots up to the configured number of parallel downloads (1–3).</summary>
    public void Start()
    {
        if (disposed) return;
        int limit = Math.Clamp(Settings.ParallelDownloads, 1, 3);
        while (workers < limit && Items.Any(i => i.State == DownloadState.Queued)) { workers++; _ = Worker(); }
    }
    private async Task Worker()
    {
        try
        {
            while (!disposed && Items.FirstOrDefault(i => i.State == DownloadState.Queued) is { } item)
                await Run(item);
        }
        finally { workers--; Notify(nameof(Summary)); if (disposed && workers == 0) { api.Dispose(); http.Dispose(); } }
    }
    private async Task Run(DownloadItem item)
    {
        var cancellation = new CancellationTokenSource(); var token = cancellation.Token;
        active[item] = cancellation;
        item.State = DownloadState.Running; item.Message = ""; Save();
        if (CheckDiskSpace(item) is { } warning) { item.State = DownloadState.Failed; item.Message = warning; active.Remove(item); cancellation.Dispose(); Save(); return; }
        try
        {
            var progress = new Progress<DownloadProgress>(p => { if (item.State != DownloadState.Running) return; item.Progress(p); Notify(nameof(TotalsLabel)); Notify(nameof(EtaLabel)); Notify(nameof(RemainingLabel)); if (DateTimeOffset.UtcNow - lastSaved > TimeSpan.FromSeconds(5)) Save(); });
            var result = await downloader.Download(item.Spec, progress, token);
            item.Progress(new(result.Bytes, result.Bytes, 0)); item.State = DownloadState.Completed;
            item.Message = (result.AlreadyComplete ? "الملف مكتمل مسبقًا؛ لم يُنزّل مجددًا." : "تم حفظ الملف.")
                + (result.SubtitleNote.Length > 0 ? " " + result.SubtitleNote : item.Spec.SubtitleLanguage.Length > 0 ? " مع ملف الترجمة (.srt)." : "");
            if (!result.AlreadyComplete) Completed?.Invoke(item);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { if (item.State == DownloadState.Running) item.State = DownloadState.Paused; }
        catch (QualityUnavailableException e) { item.State = DownloadState.Unavailable; item.Message = e.Message; }
        catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException or JsonException or UnauthorizedAccessException)
        {
            item.State = DownloadState.Failed;
            item.Message = e switch
            {
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => "الخدمة لا تسمح بتنزيل هذا الملف على الاتصال الحالي.",
                InvalidDataException => e.Message,
                UnauthorizedAccessException => "لا توجد صلاحية للكتابة في المجلد.",
                HttpRequestException or TaskCanceledException => "انقطع الاتصال أو انتهت مهلة الانتظار. اضغط متابعة لإعادة المحاولة.",
                _ => "تعذّر حفظ الملف. تحقق من المساحة والمجلد ثم اضغط متابعة."
            };
            Log.Error("download", e);
            // A dropped connection is retried by itself after a short wait (up to three times); other failures wait for the user.
            bool dropped = e is HttpRequestException { StatusCode: null } or TaskCanceledException || e is IOException io && io.Message.Contains("انقطع", StringComparison.Ordinal);
            if (dropped && item.AutoRetries < 3)
            {
                item.AutoRetries++;
                item.Message += $" ستُعاد المحاولة تلقائيًا ({item.AutoRetries}/3).";
                _ = RetryLater(item);
            }
        }
        finally { active.Remove(item); cancellation.Dispose(); Save(); }
    }
    private async Task RetryLater(DownloadItem item)
    {
        await Task.Delay(TimeSpan.FromSeconds(20 * item.AutoRetries));
        if (disposed || item.State != DownloadState.Failed) return;
        item.State = DownloadState.Queued; Save(); Start();
    }
    private void Pause(DownloadItem item)
    {
        if (!item.CanPause) return;
        item.State = DownloadState.Paused; if (active.TryGetValue(item, out var c)) c.Cancel(); Save();
    }
    private void Cancel(DownloadItem item)
    {
        if (!item.CanCancel) return;
        item.State = DownloadState.Cancelled; if (active.TryGetValue(item, out var c)) c.Cancel(); Save();
    }
    private void PauseAll() { foreach (var item in Items.Where(i => i.CanPause).ToList()) Pause(item); }
    private void Save()
    {
        try { store.Save("downloads", Items.Select(i => i.Snapshot()).ToList()); lastSaved = DateTimeOffset.UtcNow; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "تعذّر حفظ قائمة التنزيل. الملفات الجزئية باقية في مجلدها."; }
        foreach (var name in new[] { nameof(Summary), nameof(DockItem), nameof(HasDockItem), nameof(PendingCount), nameof(HasPending), nameof(TotalsLabel), nameof(Library), nameof(RemainingLabel), nameof(EtaLabel), nameof(RunningLabel), nameof(FreeSpaceLabel) }) Notify(name);
    }
    private void OpenFolder(string path)
    {
        try { System.IO.Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Status = "تعذّر فتح المجلد."; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; PauseAll(); Save();
        if (workers == 0) { api.Dispose(); http.Dispose(); }
    }
}
