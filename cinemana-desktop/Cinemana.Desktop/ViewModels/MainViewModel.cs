using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using Cinemana.Desktop.Models;
using Cinemana.Desktop.Services;

namespace Cinemana.Desktop.ViewModels;

public sealed class MainViewModel : Observable, IDisposable
{
    public LibraryStore Store { get; }
    private readonly CinemanaService service;
    private CancellationTokenSource? loading, selecting, searching;
    private List<Movie> current = [], favorites;
    private List<Movie> allEpisodes = [];
    private string section = "movies", query = "", searchText = "", status = "", heading = "أحدث الأفلام", detailsStatus = "", selectedGenre = "الكل", selectedYear = "الكل";
    private int page, sortIndex, ratingIndex, selectedSeason;
    private bool busy, canNext, detailsBusy, settingsVisible;
    private Movie? selected;
    private AppSettings settings;
    public MainViewModel(LibraryStore? store = null, CinemanaService? api = null)
    {
        Store = store ?? new(); service = api ?? new(Store.Root); favorites = Store.Favorites(); settings = Store.Load("settings", new AppSettings());
        NavigateCommand = new(async p => { searching?.Cancel(); section = p?.ToString() ?? "movies"; page = 0; ResetFilters(); await Load(); });
        SearchCommand = new(async _ => await SearchNow());
        RefreshCommand = new(async _ => await Load(true));
        NextCommand = new(async _ => { page++; await Load(); }, _ => CanNext && !IsBusy);
        PreviousCommand = new(async _ => { page--; await Load(); }, _ => page > 0 && !IsBusy);
        SelectCommand = new(async p => { if (p is Movie m) await Select(m); });
        FavoriteCommand = new(_ => ToggleFavorite(), _ => Selected is not null);
        WatchCommand = new(p => { if (p is Movie m) WatchRequested?.Invoke(m); else if (Selected is not null) WatchRequested?.Invoke(Selected); }, p => p is Movie || Selected is not null);
        DownloadCommand = new(p => { if (p is Movie m) DownloadRequested?.Invoke(m); else if (Selected is not null) DownloadRequested?.Invoke(Selected); }, p => p is Movie || Selected is not null);
        DownloadsCommand = new(_ => DownloadRequested?.Invoke(null));
        SettingsCommand = new(_ => SettingsVisible = !SettingsVisible);
        ResetFiltersCommand = new(_ => ResetFilters());
        status = Store.LoadWarning ?? "مرحبًا بك في مكتبتك السينمائية";
    }
    public event Action<Movie>? WatchRequested;
    public event Action<Movie?>? DownloadRequested;
    public ObservableCollection<Movie> Movies { get; } = [];
    public ObservableCollection<Movie> Episodes { get; } = [];
    public ObservableCollection<int> Seasons { get; } = [];
    public ObservableCollection<WatchEntry> Recent { get; } = [];
    public ObservableCollection<string> Genres { get; } = ["الكل"];
    public ObservableCollection<string> Years { get; } = ["الكل"];
    public RelayCommand NavigateCommand { get; }
    public RelayCommand SearchCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand SelectCommand { get; }
    public RelayCommand FavoriteCommand { get; }
    public RelayCommand WatchCommand { get; }
    public RelayCommand DownloadCommand { get; }
    public RelayCommand DownloadsCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand ResetFiltersCommand { get; }
    public string SearchText { get => searchText; set { if (Set(ref searchText, value)) DebounceSearch(); } }
    public string Heading { get => heading; private set => Set(ref heading, value); }
    public string Section => section;
    public Movie? Hero => Selected ?? Movies.FirstOrDefault();
    public bool HasHero => Hero is not null;
    public string Status { get => status; private set => Set(ref status, value); }
    public string DetailsStatus { get => detailsStatus; private set => Set(ref detailsStatus, value); }
    public bool IsBusy { get => busy; private set { Set(ref busy, value); RefreshCommands(); } }
    public bool DetailsBusy { get => detailsBusy; private set => Set(ref detailsBusy, value); }
    public bool SettingsVisible { get => settingsVisible; set => Set(ref settingsVisible, value); }
    public bool CanNext { get => canNext; private set { Set(ref canNext, value); RefreshCommands(); } }
    public bool IsEmpty => Movies.Count == 0 && !IsBusy;
    public string PageLabel => $"صفحة {page + 1}";
    public string FavoriteLabel => Selected is not null && favorites.Any(m => m.Id == Selected.Id) ? "♥ محفوظ في المفضلة • إزالة" : "♡ إضافة إلى المفضلة";
    public int FavoriteCount => favorites.Count;
    public Movie? Selected { get => selected; private set { Set(ref selected, value); Notify(nameof(HasSelection)); Notify(nameof(Hero)); Notify(nameof(HasHero)); Notify(nameof(FavoriteLabel)); FavoriteCommand.Refresh(); WatchCommand.Refresh(); DownloadCommand.Refresh(); } }
    public bool HasSelection => Selected is not null;
    public string SelectedGenre { get => selectedGenre; set { if (Set(ref selectedGenre, value)) ApplyFilters(); } }
    public string SelectedYear { get => selectedYear; set { if (Set(ref selectedYear, value)) ApplyFilters(); } }
    public int SortIndex { get => sortIndex; set { if (Set(ref sortIndex, value)) ApplyFilters(); } }
    public int RatingIndex { get => ratingIndex; set { if (Set(ref ratingIndex, value)) ApplyFilters(); } }
    public int SelectedSeason { get => selectedSeason; set { if (Set(ref selectedSeason, value)) UpdateEpisodes(); } }
    public double CardWidth { get => settings.CardWidth; set { settings = settings with { CardWidth = value }; Notify(); SaveSettings(); } }
    public double TextSize { get => settings.TextSize; set { settings = settings with { TextSize = value }; Notify(); SaveSettings(); } }
    public bool CacheImages { get => settings.CacheImages; set { settings = settings with { CacheImages = value }; Notify(); SaveSettings(); } }
    private void SaveSettings() { try { Store.Save("settings", settings); } catch (Exception e) when (StorageError(e)) { Status = "تعذّر حفظ الإعدادات على القرص."; } }
    private static bool StorageError(Exception e) => e is IOException or UnauthorizedAccessException;
    private void RefreshCommands() { NextCommand?.Refresh(); PreviousCommand?.Refresh(); Notify(nameof(IsEmpty)); Notify(nameof(PageLabel)); }
    private async void DebounceSearch()
    {
        searching?.Cancel(); searching?.Dispose(); searching = new(); var token = searching.Token;
        try { await Task.Delay(500, token); if (SearchText.Trim().Length >= 2) await SearchNow(); }
        catch (OperationCanceledException) { }
    }
    private async Task SearchNow()
    {
        searching?.Cancel();
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        section = "search"; query = SearchText.Trim(); page = 0; ResetFilters(); await Load();
    }
    public void RefreshHistory()
    { Recent.Clear(); foreach (var e in Store.History().Take(12)) Recent.Add(e); }
    public async Task Load(bool refresh = false)
    {
        loading?.Cancel(); loading?.Dispose(); loading = new(); var token = loading.Token;
        IsBusy = true; Status = "جارٍ تحميل العناوين…"; CanNext = false;
        // Clear the previous section immediately; failed navigation must not masquerade as new results.
        Movies.Clear(); current = [];
        selecting?.Cancel(); Selected = null; Episodes.Clear(); Seasons.Clear(); Notify(nameof(Section));
        Heading = section switch { "series" => "أحدث المسلسلات", "favorites" => "مفضلتك", "history" => "سجل المشاهدة", "search" => $"نتائج «{query}»", _ => "أحدث الأفلام" };
        try
        {
            RefreshHistory();
            CacheResult<List<Movie>> result = section switch
            {
                "favorites" => new([.. favorites]),
                "history" => new(Store.History().Select(e => e.Movie with { ProgressText = e.ProgressLabel }).ToList()),
                _ => await service.Catalog(section, page, query, refresh, false, token)
            };
            token.ThrowIfCancellationRequested();
            current = result.Value; CanNext = section is not ("favorites" or "history") && current.Count >= 24;
            var oldGenre = SelectedGenre; var oldYear = SelectedYear;
            Genres.Clear(); Genres.Add("الكل"); foreach (var g in current.SelectMany(m => m.Genres.Split('،', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct().Order()) Genres.Add(g);
            Years.Clear(); Years.Add("الكل"); foreach (var y in current.Select(m => m.Year).Where(y => y.Length > 0).Distinct().OrderDescending()) Years.Add(y);
            SelectedGenre = Genres.Contains(oldGenre) ? oldGenre : "الكل"; SelectedYear = Years.Contains(oldYear) ? oldYear : "الكل";
            ApplyFilters();
            if (Movies.FirstOrDefault() is { } first) _ = Select(first);
            Status = result.IsStale ? $"الاتصال غير متاح • نعرض نسخة محفوظة بتاريخ {result.SavedAt?.ToLocalTime():yyyy/MM/dd HH:mm}" : $"{current.Count} عنوانًا • {(section is "favorites" or "history" ? "مكتبتك المحلية" : "سينمانا")} • الفلاتر والترتيب يطبّقان على الصفحة الحالية";
            if (CacheImages) _ = CachePosters(current.ToList(), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException || StorageError(e))
        { if (!token.IsCancellationRequested) Status = "تعذّر الاتصال بسينمانا. تحقق من الشبكة ثم اضغط إعادة المحاولة. يمكنك فتح المفضلة والسجل دون اتصال."; }
        finally { if (!token.IsCancellationRequested) { IsBusy = false; Notify(nameof(IsEmpty)); RefreshCommands(); } }
    }
    private async Task CachePosters(List<Movie> source, CancellationToken token)
    {
        try
        {
            var cached = await Task.WhenAll(source.Select(m => service.WithImage(m, token)));
            token.ThrowIfCancellationRequested(); current = cached.ToList(); ApplyFilters();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (StorageError(e) || e is HttpRequestException) { }
    }
    public static IEnumerable<Movie> Filter(IEnumerable<Movie> source, string genre, string year, int minimumRating, int order)
    {
        var result = source.Where(m => (genre == "الكل" || m.Genres.Split('،', StringSplitOptions.TrimEntries).Contains(genre)) && (year == "الكل" || m.Year == year) && m.NumericRating >= minimumRating);
        return order switch { 1 => result.OrderByDescending(m => m.NumericRating), 2 => result.OrderByDescending(m => m.Year), 3 => result.OrderBy(m => m.DisplayTitle), _ => result };
    }
    private void ApplyFilters()
    {
        Movies.Clear(); foreach (var movie in Filter(current, selectedGenre ?? "الكل", selectedYear ?? "الكل", RatingIndex == 0 ? 0 : RatingIndex + 5, SortIndex)) Movies.Add(movie);
        Notify(nameof(IsEmpty));
        Notify(nameof(Hero)); Notify(nameof(HasHero));
    }
    private void ResetFilters() { selectedGenre = selectedYear = "الكل"; sortIndex = ratingIndex = 0; Notify(nameof(SelectedGenre)); Notify(nameof(SelectedYear)); Notify(nameof(SortIndex)); Notify(nameof(RatingIndex)); ApplyFilters(); }
    public async Task Select(Movie movie)
    {
        selecting?.Cancel(); selecting?.Dispose(); selecting = new(); var token = selecting.Token;
        Selected = movie; DetailsBusy = true; DetailsStatus = "جارٍ جلب التفاصيل…"; Episodes.Clear(); Seasons.Clear(); allEpisodes = [];
        try
        {
            var full = await service.Details(movie.Id, token);
            token.ThrowIfCancellationRequested(); Selected = full with { LocalPoster = movie.LocalPoster };
            DetailsStatus = "";
            if (full.Kind == "2")
            {
                allEpisodes = await service.Episodes(movie.Id, token); token.ThrowIfCancellationRequested();
                foreach (var season in allEpisodes.Select(m => m.Season).Distinct()) Seasons.Add(season);
                selectedSeason = 0; SelectedSeason = Seasons.FirstOrDefault(); UpdateEpisodes();
                if (allEpisodes.Count == 0) DetailsStatus = "تعذّر العثور على قائمة الحلقات لهذا العنوان حاليًا.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        { if (!token.IsCancellationRequested) DetailsStatus = "تعذّر تحديث التفاصيل. يمكنك استخدام البيانات المتاحة أو إعادة اختيار العنوان."; }
        finally { if (!token.IsCancellationRequested) DetailsBusy = false; }
    }
    private void UpdateEpisodes() { Episodes.Clear(); foreach (var episode in allEpisodes.Where(m => m.Season == SelectedSeason)) Episodes.Add(episode); }
    private void ToggleFavorite()
    {
        if (Selected is null) return;
        var updated = favorites.Where(m => m.Id != Selected.Id).ToList(); if (updated.Count == favorites.Count) updated.Insert(0, Selected);
        try { Store.Save("favorites", updated); favorites = updated; Notify(nameof(FavoriteLabel)); Notify(nameof(FavoriteCount)); if (section == "favorites") { loading?.Cancel(); current = [.. favorites]; ApplyFilters(); } }
        catch (Exception e) when (StorageError(e)) { Status = "تعذّر حفظ المفضلة. تحقق من المساحة وصلاحية الكتابة."; }
    }
    public void Dispose() { loading?.Cancel(); selecting?.Cancel(); searching?.Cancel(); service.Dispose(); loading?.Dispose(); selecting?.Dispose(); searching?.Dispose(); }
}
