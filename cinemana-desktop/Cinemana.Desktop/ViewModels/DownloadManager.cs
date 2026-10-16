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
    public string QualityLabel => Spec.Quality + "p" + (Spec.SubtitleLanguage.Length > 0 ? " + SRT" : "");
    public string Destination => Spec.Destination;
    public DownloadState State { get => state; set { Set(ref state, value); Refresh(); } }
    public string Message { get => message; set => Set(ref message, value); }
    public string StatusLabel => State switch { DownloadState.Queued => "في الانتظار", DownloadState.Running => "جارٍ التنزيل", DownloadState.Paused => "متوقف مؤقتًا", DownloadState.Completed => "مكتمل", DownloadState.Failed => "تعذّر التنزيل", DownloadState.Cancelled => "ملغي", _ => "الجودة غير متاحة" };
    public double Percent => total > 0 ? Math.Clamp(bytes * 100d / total.Value, 0, 100) : 0;
    public bool Indeterminate => State == DownloadState.Running && total is null;
    public string ProgressLabel => DownloadPaths.Size(bytes) + (total.HasValue ? " / " + DownloadPaths.Size(total.Value) : "") + (speed > 0 && State == DownloadState.Running ? " • " + DownloadPaths.Size((long)speed) + "/s" : "");
    public bool CanPause => State is DownloadState.Running or DownloadState.Queued;
    public bool CanResume => State is DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled or DownloadState.Unavailable;
    public bool CanCancel => State is not (DownloadState.Completed or DownloadState.Cancelled);
    public void Progress(DownloadProgress value) { bytes = value.Bytes; total = value.Total; speed = value.BytesPerSecond; Refresh(); }
    private void Refresh() { Notify(nameof(StatusLabel)); Notify(nameof(Percent)); Notify(nameof(Indeterminate)); Notify(nameof(ProgressLabel)); Notify(nameof(CanPause)); Notify(nameof(CanResume)); Notify(nameof(CanCancel)); }
    public DownloadSnapshot Snapshot() => new(Spec, State, bytes, total, Message);
}

public sealed class DownloadManager : Observable, IDisposable
{
    private readonly LibraryStore store;
    private readonly CinemanaService api;
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly FileDownloader downloader;
    private CancellationTokenSource? activeCancellation;
    private DownloadItem? active;
    private bool pumping, disposed;
    private DateTimeOffset lastSaved;
    private string directory;
    private string status = "التنزيلات تستمر أثناء استخدام التطبيق.";
    public ObservableCollection<DownloadItem> Items { get; } = [];
    public DownloadManager(LibraryStore store)
    {
        this.store = store; api = new(store.Root); downloader = new(http, (movie, token) => api.Playback(movie.Id, token));
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (videos.Length == 0) videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
        directory = store.Load("download-directory", Path.Combine(videos, "Cinemana"));
        foreach (var saved in store.Load("downloads", new List<DownloadSnapshot>()))
            Items.Add(new(saved with { State = saved.State is DownloadState.Running or DownloadState.Queued ? DownloadState.Paused : saved.State }));
        PauseCommand = new(p => { if (p is DownloadItem item) Pause(item); });
        ResumeCommand = new(p => { if (p is DownloadItem item && item.CanResume) { item.State = DownloadState.Queued; item.Message = ""; Save(); Start(); } });
        CancelCommand = new(p => { if (p is DownloadItem item) Cancel(item); });
        PauseAllCommand = new(_ => PauseAll()); ResumeAllCommand = new(_ => { foreach (var item in Items.Where(i => i.State == DownloadState.Paused)) item.State = DownloadState.Queued; Save(); Start(); });
        FolderCommand = new(p => { if (p is DownloadItem item) OpenFolder(Path.GetDirectoryName(item.Destination)!); else OpenFolder(Directory); });
    }
    public string Directory { get => directory; set { if (Set(ref directory, Path.GetFullPath(value))) { try { store.Save("download-directory", directory); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "تعذّر حفظ مجلد التنزيل."; } } } }
    public string Status { get => status; private set => Set(ref status, value); }
    public DownloadItem? DockItem => Items.FirstOrDefault(i => i.State == DownloadState.Running) ?? Items.FirstOrDefault(i => i.State == DownloadState.Paused) ?? Items.LastOrDefault();
    public string Summary => $"{Items.Count(i => i.State == DownloadState.Completed)} مكتمل • {Items.Count(i => i.State == DownloadState.Running)} جارٍ • {Items.Count(i => i.State == DownloadState.Queued)} في الانتظار";
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand PauseAllCommand { get; }
    public RelayCommand ResumeAllCommand { get; }
    public RelayCommand FolderCommand { get; }
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
    private void Start() { if (!pumping && !disposed) _ = Pump(); }
    private async Task Pump()
    {
        pumping = true;
        try
        {
            while (!disposed && Items.FirstOrDefault(i => i.State == DownloadState.Queued) is { } item)
            {
                active = item; activeCancellation = new(); var token = activeCancellation.Token;
                item.State = DownloadState.Running; item.Message = ""; Save();
                try
                {
                    var progress = new Progress<DownloadProgress>(p => { if (item.State != DownloadState.Running) return; item.Progress(p); if (DateTimeOffset.UtcNow - lastSaved > TimeSpan.FromSeconds(5)) Save(); });
                    var result = await downloader.Download(item.Spec, progress, token);
                    item.Progress(new(result.Bytes, result.Bytes, 0)); item.State = DownloadState.Completed;
                    item.Message = (result.AlreadyComplete ? "الملف مكتمل مسبقًا؛ لم يُنزّل مجددًا." : "تم حفظ الملف.")
                        + (result.SubtitleNote.Length > 0 ? " " + result.SubtitleNote : item.Spec.SubtitleLanguage.Length > 0 ? " مع ملف الترجمة (.srt)." : "");
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
                }
                finally { activeCancellation.Dispose(); activeCancellation = null; active = null; Save(); }
            }
        }
        finally { pumping = false; Notify(nameof(Summary)); if (disposed) { api.Dispose(); http.Dispose(); } }
    }
    private void Pause(DownloadItem item)
    {
        if (!item.CanPause) return;
        item.State = DownloadState.Paused; if (ReferenceEquals(active, item)) activeCancellation?.Cancel(); Save();
    }
    private void Cancel(DownloadItem item)
    {
        if (!item.CanCancel) return;
        item.State = DownloadState.Cancelled; if (ReferenceEquals(active, item)) activeCancellation?.Cancel(); Save();
    }
    private void PauseAll() { foreach (var item in Items.Where(i => i.CanPause).ToList()) Pause(item); }
    private void Save()
    {
        try { store.Save("downloads", Items.Select(i => i.Snapshot()).ToList()); lastSaved = DateTimeOffset.UtcNow; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "تعذّر حفظ قائمة التنزيل. الملفات الجزئية باقية في مجلدها."; }
        Notify(nameof(Summary));
        Notify(nameof(DockItem));
    }
    private void OpenFolder(string path)
    {
        try { System.IO.Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Status = "تعذّر فتح المجلد."; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; PauseAll(); activeCancellation?.Cancel(); Save();
        if (!pumping) { api.Dispose(); http.Dispose(); }
    }
}
