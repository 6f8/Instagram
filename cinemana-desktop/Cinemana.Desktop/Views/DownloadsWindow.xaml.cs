using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Cinemana.Desktop.Models;
using Cinemana.Desktop.Services;
using Cinemana.Desktop.ViewModels;
using Microsoft.Win32;

namespace Cinemana.Desktop.Views;

/// <summary>One episode in the pick list of the downloads window.</summary>
public sealed class EpisodeChoice(Movie movie, bool selected) : Observable
{
    private bool picked = selected;
    public Movie Movie { get; } = movie;
    public string Label => Movie.EpisodeLabel;
    public bool Selected { get => picked; set { if (Set(ref picked, value)) Changed?.Invoke(); } }
    public event Action? Changed;
}

public partial class DownloadsWindow : Window
{
    private readonly DownloadManager manager;
    private readonly CinemanaService api;
    private CancellationTokenSource? operation;
    private Movie? target;
    private List<Movie> episodes = [];
    private DownloadPlan? plan;
    private List<Movie>? only;
    private bool initializing, closed;
    private static readonly HttpClient Sizes = new() { Timeout = TimeSpan.FromSeconds(8) };
    public System.Collections.ObjectModel.ObservableCollection<EpisodeChoice> Choices { get; } = [];
    public DownloadsWindow(DownloadManager manager, string root)
    {
        this.manager = manager; api = new(root);
        InitializeComponent(); DataContext = manager; EpisodeList.ItemsSource = Choices;
        Width = Math.Min(1060, SystemParameters.WorkArea.Width - 32); Height = Math.Min(780, SystemParameters.WorkArea.Height - 32);
        PlanPanel.Visibility = Visibility.Collapsed;
        Closed += (_, _) => { closed = true; operation?.Cancel(); operation?.Dispose(); api.Dispose(); };
    }
    /// <summary>
    /// Opens ready to go: a series starts on its current season, best quality and Arabic subtitles, and the
    /// preview runs at once so "Start" is one click away. <paramref name="episodes"/> limits it to those episodes.
    /// </summary>
    public async Task SetTarget(Movie movie, int selectedSeason, List<Movie>? episodes = null)
    {
        only = episodes is { Count: > 0 } ? episodes : null;
        operation?.Cancel(); operation?.Dispose(); operation = new(); var token = operation.Token;
        initializing = true; target = movie; plan = null; episodes = [];
        PlanPanel.Visibility = Visibility.Visible; TargetLabel.Text = movie.DisplayTitle;
        PrepareButton.IsEnabled = StartButton.IsEnabled = false; Scope.SelectedIndex = 0;
        bool series = movie.Kind == "2";
        SeasonScope.Visibility = SeriesScope.Visibility = Season.Visibility = SeasonLabel.Visibility = series ? Visibility.Visible : Visibility.Collapsed;
        Quality.ItemsSource = null; Season.ItemsSource = null; PlanStatus.Text = "جارٍ جلب الجودات والمواسم…";
        try
        {
            var playbackTask = api.Playback(movie.Id, token);
            var episodesTask = series ? api.Episodes(movie.Id, token) : Task.FromResult(new List<Movie>());
            await Task.WhenAll(playbackTask, episodesTask); token.ThrowIfCancellationRequested();
            var playback = await playbackTask; episodes = await episodesTask;
            var choices = new List<QualityChoice> { new(-2, "أفضل جودة متاحة"), new(-1, "جميع الجودات المتاحة") };
            choices.AddRange(playback.Sources.Where(s => s.CanDownload).Select(s => new QualityChoice(s.Height, s.Label)));
            Quality.ItemsSource = choices; Quality.SelectedIndex = 0;
            var seasons = episodes.Select(e => e.Season).Distinct().Order().ToList(); Season.ItemsSource = seasons;
            Season.SelectedItem = seasons.Contains(selectedSeason) ? selectedSeason : seasons.FirstOrDefault();
            if (playback.Sources.All(s => !s.CanDownload)) { PlanStatus.Text = "لا يتوفر ملف مباشر قابل للتنزيل لهذا العنوان."; return; }
            PrepareButton.IsEnabled = true;
            PlanStatus.Text = series ? $"{episodes.Count} حلقة متاحة عبر {seasons.Count} موسم." : "";
            if (series && only is null) Scope.SelectedIndex = 1;
            FillChoices();
            UpdateFolderLabel();
            initializing = false;
            PrepareClick(this, new RoutedEventArgs());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        { if (!closed && !token.IsCancellationRequested) PlanStatus.Text = "تعذّر جلب خيارات التنزيل. أعد فتح التنزيل من العنوان للمحاولة مجددًا."; }
        finally { if (!token.IsCancellationRequested) initializing = false; }
    }
    /// <summary>Episodes of the chosen scope with a tick each; unwatched-only requests start with just those ticked.</summary>
    private void FillChoices()
    {
        Choices.Clear();
        if (target?.Kind != "2" || Scope.SelectedIndex == 0) { EpisodeList.Visibility = PickRow.Visibility = Visibility.Collapsed; return; }
        int season = Season.SelectedItem is int s ? s : target.Season;
        var list = Scope.SelectedIndex == 1 ? episodes.Where(ep => ep.Season == season) : episodes;
        var keep = only?.Select(m => m.Id).ToHashSet();
        foreach (var ep in list)
        {
            var choice = new EpisodeChoice(ep, keep is null || keep.Contains(ep.Id));
            choice.Changed += () => { plan = null; StartButton.IsEnabled = false; PlanStatus.Text = $"اخترت {Choices.Count(c => c.Selected)} حلقة. اضغط معاينة ملفات التنزيل."; };
            Choices.Add(choice);
        }
        EpisodeList.Visibility = PickRow.Visibility = Choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void PickAllClick(object sender, RoutedEventArgs e) { foreach (var c in Choices) c.Selected = true; }
    private void PickNoneClick(object sender, RoutedEventArgs e) { foreach (var c in Choices) c.Selected = false; }
    private string Folder => target is null ? manager.Directory : manager.FolderFor(target.Title);
    private void UpdateFolderLabel() => SeriesFolderLabel.Text = target is not null && Folder != manager.Directory ? "مجلد خاص بهذا العنوان: " + Folder : "";
    private void ChooseSeriesFolder(object sender, RoutedEventArgs e)
    {
        if (target is null) return;
        var chooser = new OpenFolderDialog { Title = "مجلد خاص بـ " + target.DisplayTitle, Multiselect = false };
        if (chooser.ShowDialog(this) != true) return;
        manager.SetFolderFor(target.Title, chooser.FolderName); UpdateFolderLabel();
        plan = null; StartButton.IsEnabled = false; PlanStatus.Text = "تم تعيين مجلد لهذا العنوان. أعد المعاينة.";
    }
    private void ResetSeriesFolder(object sender, RoutedEventArgs e) { if (target is null) return; manager.SetFolderFor(target.Title, null); UpdateFolderLabel(); }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "تصدير قائمة التنزيل", Filter = "قائمة سينمانا (*.json)|*.json", FileName = "cinemana-queue.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { manager.Export(dialog.FileName); PlanStatus.Text = "صُدّرت القائمة."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PlanStatus.Text = "تعذّر حفظ الملف."; }
    }
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "استيراد قائمة التنزيل", Filter = "قائمة سينمانا (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { PlanStatus.Text = $"استُورد {manager.Import(dialog.FileName)} ملفًا متوقفًا مؤقتًا. اضغط «متابعة المتوقف» للبدء."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { PlanStatus.Text = "الملف ليس قائمة تنزيل صالحة."; }
    }
    private static async Task<long?> HeadSize(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await Sizes.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength : null;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException) { return null; }
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || StartButton is null) return;
        if (sender == Scope || sender == Season) FillChoices();
        operation?.Cancel(); plan = null; StartButton.IsEnabled = false; if (sender != Scope && sender != Season) only = null;
        PlanStatus.Text = "تغيّرت الخيارات. اضغط معاينة ملفات التنزيل.";
    }
    private void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var chooser = new OpenFolderDialog { Title = "اختر مجلد حفظ الأفلام والمسلسلات", Multiselect = false };
        if (Directory.Exists(manager.Directory)) chooser.InitialDirectory = manager.Directory;
        if (chooser.ShowDialog(this) == true) { manager.Directory = chooser.FolderName; plan = null; StartButton.IsEnabled = false; PlanStatus.Text = "تم تغيير مجلد الحفظ. أعد المعاينة."; }
    }
    private async void PrepareClick(object sender, RoutedEventArgs e)
    {
        if (target is null || Quality.SelectedItem is not QualityChoice quality) return;
        operation?.Cancel(); operation?.Dispose(); operation = new(); var token = operation.Token;
        string subtitle = (SubtitleLanguage.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        int scope = Scope.SelectedIndex; int season = Season.SelectedItem is int s ? s : target.Season;
        var selected = Choices.Count > 0 ? Choices.Where(c => c.Selected).Select(c => c.Movie).ToList() : only is not null ? only : scope switch { 1 => episodes.Where(ep => ep.Season == season).ToList(), 2 => episodes, _ => new List<Movie> { target } };
        if (only is not null) TargetLabel.Text = $"{target.DisplayTitle} • {only.Count} حلقة لم تُشاهَد";
        if (selected.Count == 0) { PlanStatus.Text = "لا توجد حلقات متاحة للنطاق المحدد."; return; }
        plan = null; StartButton.IsEnabled = PrepareButton.IsEnabled = false; CancelPlanButton.IsEnabled = true;
        Scope.IsEnabled = Quality.IsEnabled = Season.IsEnabled = SubtitleLanguage.IsEnabled = FolderButton.IsEnabled = false;
        try
        {
            var planner = new DownloadPlanner((movie, ct) => api.Playback(movie.Id, ct), HeadSize);
            string groupId = target.Kind == "2" ? episodes.FirstOrDefault()?.Id ?? target.Id : target.Id;
            var progress = new Progress<(int Done, int Total)>(p => { if (!token.IsCancellationRequested) PlanStatus.Text = $"فحص الجودات: {p.Done} / {p.Total}…"; });
            var result = await planner.Build(selected, target.Title, groupId, Folder, quality.Height, subtitle, progress, token);
            token.ThrowIfCancellationRequested(); plan = result;
            int count = result.Files.Select(f => f.Movie.Id).Distinct().Count();
            string size = result.KnownBytes > 0 ? $" الحجم الكلي نحو {DownloadPaths.Size(result.KnownBytes)}" + (result.UnknownSizes > 0 ? $" (+{result.UnknownSizes} ملف لم يُعلن حجمه)" : "") + "." : "";
            PlanStatus.Text = $"المعاينة: {result.Files.Count} ملفًا لـ {count} عنوانًا/حلقة.{size} {manager.FreeSpaceLabel}";
            if (result.Notices.Count > 0) PlanStatus.Text += $"\nتعذّر تجهيز {result.Notices.Count} عنوانًا أو جودة. " + string.Join("\n", result.Notices.Take(3));
            StartButton.Content = $"بدء تنزيل {result.Files.Count} ملفًا"; StartButton.IsEnabled = result.Files.Count > 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { if (!closed) PlanStatus.Text = "أُلغيت المعاينة. لم يبدأ تنزيل أي ملف."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!closed) PlanStatus.Text = "تعذّر تجهيز المجلد أو أسماء الملفات. اختر مجلدًا آخر."; }
        finally { if (!closed) { Scope.IsEnabled = Quality.IsEnabled = Season.IsEnabled = SubtitleLanguage.IsEnabled = FolderButton.IsEnabled = true; PrepareButton.IsEnabled = true; CancelPlanButton.IsEnabled = false; } }
    }
    private void StartClick(object sender, RoutedEventArgs e)
    {
        if (plan is null) return;
        int added = manager.Add(plan.Files); PlanStatus.Text = $"أُضيف {added} ملفًا. يمكنك متابعة التنزيلات أدناه.";
        plan = null; StartButton.IsEnabled = false;
    }
    private void CancelPlanClick(object sender, RoutedEventArgs e) => operation?.Cancel();
    private void HistoryClick(object sender, RoutedEventArgs e)
    {
        bool show = HistoryList.Visibility != Visibility.Visible;
        HistoryList.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        QueueList.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        HistoryToggle.Content = show ? "القائمة" : "السجل";
    }
}
