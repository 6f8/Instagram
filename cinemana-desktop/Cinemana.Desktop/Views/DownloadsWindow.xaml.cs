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
    public DownloadsWindow(DownloadManager manager, string root)
    {
        this.manager = manager; api = new(root);
        InitializeComponent(); DataContext = manager;
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
            initializing = false;
            PrepareClick(this, new RoutedEventArgs());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        { if (!closed && !token.IsCancellationRequested) PlanStatus.Text = "تعذّر جلب خيارات التنزيل. أعد فتح التنزيل من العنوان للمحاولة مجددًا."; }
        finally { if (!token.IsCancellationRequested) initializing = false; }
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || StartButton is null) return;
        operation?.Cancel(); plan = null; StartButton.IsEnabled = false; only = null;
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
        var selected = only is not null ? only : scope switch { 1 => episodes.Where(ep => ep.Season == season).ToList(), 2 => episodes, _ => new List<Movie> { target } };
        if (only is not null) TargetLabel.Text = $"{target.DisplayTitle} • {only.Count} حلقة لم تُشاهَد";
        if (selected.Count == 0) { PlanStatus.Text = "لا توجد حلقات متاحة للنطاق المحدد."; return; }
        plan = null; StartButton.IsEnabled = PrepareButton.IsEnabled = false; CancelPlanButton.IsEnabled = true;
        Scope.IsEnabled = Quality.IsEnabled = Season.IsEnabled = SubtitleLanguage.IsEnabled = FolderButton.IsEnabled = false;
        try
        {
            var planner = new DownloadPlanner((movie, ct) => api.Playback(movie.Id, ct));
            string groupId = target.Kind == "2" ? episodes.FirstOrDefault()?.Id ?? target.Id : target.Id;
            var progress = new Progress<(int Done, int Total)>(p => { if (!token.IsCancellationRequested) PlanStatus.Text = $"فحص الجودات: {p.Done} / {p.Total}…"; });
            var result = await planner.Build(selected, target.Title, groupId, manager.Directory, quality.Height, subtitle, progress, token);
            token.ThrowIfCancellationRequested(); plan = result;
            int count = result.Files.Select(f => f.Movie.Id).Distinct().Count();
            PlanStatus.Text = $"المعاينة: {result.Files.Count} ملفًا لـ {count} عنوانًا/حلقة. الحجم والسرعة سيظهران عند بدء كل ملف.";
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
}
