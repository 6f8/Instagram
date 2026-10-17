using System.Collections.ObjectModel;
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
    private string section = "home", query = "", searchText = "", status = "", heading = "الرئيسية", detailsStatus = "", selectedGenre = "الكل", selectedYear = "الكل";
    private int page, sortIndex, ratingIndex, selectedSeason, heroIndex;
    private bool busy, canNext, detailsBusy, settingsVisible;
    private Movie? selected;
    private AppSettings settings;
    public MainViewModel(LibraryStore? store = null, CinemanaService? api = null)
    {
        Store = store ?? new(); service = api ?? new(Store.Root); favorites = Store.Favorites(); settings = Store.Load("settings", new AppSettings());
        NavigateCommand = new(async p => { searching?.Cancel(); await GoTo(p?.ToString() ?? "home"); });
        BackCommand = new(async _ => await Back(), _ => CanGoBack);
        PickSuggestionCommand = new(async p => { if (p is Movie m) { SuggestionsOpen = false; await Select(m); SelectionRequested?.Invoke(); } else if (p is string q) { SearchText = q; await SearchNow(); } });
        ContinueCommand = new(_ => { if (NextToWatch is { } m) WatchRequested?.Invoke(m); }, _ => NextToWatch is not null);
        MarkWatchedCommand = new(p =>
        {
            if (p is not Movie m) return;
            // Toggle: a watched episode becomes unwatched again.
            bool watched = Store.History().Any(h => h.Movie.Id == m.Id && h.Percent >= 95);
            try { if (watched) Store.RemoveProgress(m.Id); else Store.SaveProgress(m, 1, 1); } catch (Exception e) when (StorageError(e)) { Status = "تعذّر حفظ حالة المشاهدة."; }
            UpdateEpisodes(); RefreshHistory(); NotifyContinue();
        });
        DownloadUnwatchedCommand = new(_ => { if (Selected is { } s) UnwatchedDownloadRequested?.Invoke(s, UnwatchedEpisodes().ToList()); }, _ => Selected?.Kind == "2" && allEpisodes.Count > 0);
        SearchCommand = new(async _ => await SearchNow());
        RefreshCommand = new(async _ => await Load(true));
        NextCommand = new(async _ => { page++; await Load(); }, _ => CanNext && !IsBusy);
        PreviousCommand = new(async _ => { page--; await Load(); }, _ => page > 0 && !IsBusy);
        SelectCommand = new(async p => { if (p is Movie m) { DetailsOpen = true; await Select(m); SelectionRequested?.Invoke(); } });
        MoviesView = System.Windows.Data.CollectionViewSource.GetDefaultView(Movies);
        FavoriteCommand = new(_ => ToggleFavorite(), _ => Selected is not null);
        WatchCommand = new(p => { if (p is Movie m) WatchRequested?.Invoke(m); else if (Selected is not null) WatchRequested?.Invoke(Selected); }, p => p is Movie || Selected is not null);
        DownloadCommand = new(p => { if (p is Movie m) DownloadRequested?.Invoke(m); else if (Selected is not null) DownloadRequested?.Invoke(Selected); }, p => p is Movie || Selected is not null);
        DownloadsCommand = new(_ => DownloadRequested?.Invoke(null));
        SettingsCommand = new(_ => SettingsVisible = !SettingsVisible);
        ResetFiltersCommand = new(_ => ResetFilters());
        NextHeroCommand = new(_ => MoveHero(1));
        PreviousHeroCommand = new(_ => MoveHero(-1));
        HeroDotCommand = new(p => { if (p is Movie m && HeroItems.IndexOf(m) is >= 0 and var i) { heroIndex = i; NotifyHero(); } });
        status = Store.LoadWarning ?? "مرحبًا بك في مكتبتك السينمائية";
        foreach (var q in settings.RecentSearches) RecentSearches.Add(q);
        section = settings.LastSection is "home" or "movies" or "series" or "favorites" or "history" or "library" ? settings.LastSection : "home";
    }
    public event Action<Movie>? WatchRequested;
    /// <summary>A title picked from the search list: the window opens its details panel.</summary>
    public event Action? SelectionRequested;
    public event Action<Movie, List<Movie>>? UnwatchedDownloadRequested;
    public RelayCommand BackCommand { get; }
    public RelayCommand PickSuggestionCommand { get; }
    public RelayCommand ContinueCommand { get; }
    public RelayCommand DownloadUnwatchedCommand { get; }
    public RelayCommand MarkWatchedCommand { get; }
    // --- Back navigation: sections visited, newest last ---
    private readonly Stack<string> history = new();
    public bool CanGoBack => history.Count > 0;
    private async Task GoTo(string target, bool remember = true)
    {
        if (remember && target != section) { history.Push(section); BackCommand.Refresh(); }
        section = target; page = 0; ResetFilters();
        if (target != "search") Change(settings with { LastSection = target }, nameof(Section));
        await Load();
    }
    /// <summary>Esc / Alt+← / back button: closes the open title first, then returns to the previous section.</summary>
    public async Task<bool> Back()
    {
        if (Selected is not null && (IsHome || DetailsOpen)) { ClearSelection(); DetailsOpen = false; return true; }
        if (history.Count == 0) return false;
        string previous = history.Pop(); BackCommand.Refresh();
        await GoTo(previous, remember: false);
        return true;
    }
    private bool detailsOpen;
    public bool DetailsOpen { get => detailsOpen; set => Set(ref detailsOpen, value); }
    // --- Notices shown in a strip under the header ---
    private string serviceNotice = "", updateNotice = "", updateUrl = "";
    public string ServiceNotice { get => serviceNotice; set { Set(ref serviceNotice, value); Notify(nameof(HasServiceNotice)); } }
    public bool HasServiceNotice => serviceNotice.Length > 0;
    public string UpdateNotice { get => updateNotice; set { Set(ref updateNotice, value); Notify(nameof(HasUpdate)); } }
    public string UpdateUrl { get => updateUrl; set => Set(ref updateUrl, value); }
    public bool HasUpdate => updateNotice.Length > 0;
    public async Task CheckServiceAndUpdates()
    {
        ServiceNotice = await service.Health(CancellationToken.None) ?? "";
        if (await UpdateCheck.Find(CancellationToken.None) is { } update)
        { UpdateNotice = $"يتوفر إصدار جديد {update.Version.ToString(3)}"; UpdateUrl = update.Url; Log.Info($"update available {update.Version}"); }
    }
    // --- Instant search suggestions and recent searches ---
    public ObservableCollection<Movie> Suggestions { get; } = [];
    public ObservableCollection<string> RecentSearches { get; } = [];
    private bool suggestionsOpen;
    public bool SuggestionsOpen { get => suggestionsOpen; set => Set(ref suggestionsOpen, value); }
    private void RememberSearch(string text)
    {
        var list = settings.RecentSearches.Where(q => q != text).Prepend(text).Take(8).ToList();
        Change(settings with { RecentSearches = list }, nameof(RecentSearches));
        RecentSearches.Clear(); foreach (var q in list) RecentSearches.Add(q);
    }
    public void ShowRecent() { if (SearchText.Length == 0 && RecentSearches.Count > 0) { Suggestions.Clear(); SuggestionsOpen = true; } }
    // --- "Continue" for a series: the first episode after the last one watched ---
    public Movie? NextToWatch
    {
        get
        {
            if (Selected is null) return null;
            if (Selected.Kind != "2" || allEpisodes.Count == 0) return Selected;
            var seen = Store.History().DistinctBy(e => e.Movie.Id).ToDictionary(e => e.Movie.Id, e => e.Percent);
            int last = allEpisodes.FindLastIndex(e => seen.ContainsKey(e.Id));
            if (last < 0) return allEpisodes[0];
            return seen[allEpisodes[last].Id] < 95 || last + 1 >= allEpisodes.Count ? allEpisodes[last] : allEpisodes[last + 1];
        }
    }
    public string ContinueLabel => NextToWatch is { Kind: "2" } m ? $"تابع: م{m.Season} ح{m.Episode}" : "شاهد الآن";
    public IEnumerable<Movie> UnwatchedEpisodes()
    {
        var seen = Store.History().Where(e => e.Percent >= 95).Select(e => e.Movie.Id).ToHashSet();
        return allEpisodes.Where(e => !seen.Contains(e.Id));
    }
    public event Action<Movie?>? DownloadRequested;
    /// <summary>Completed downloads for "My library"; supplied by the window that owns the download manager.</summary>
    public Func<IEnumerable<Movie>> LibraryProvider { get; set; } = () => [];
    public ObservableCollection<Movie> Movies { get; } = [];
    /// <summary>The section grid; in "My library" it is grouped by series or film title.</summary>
    public System.ComponentModel.ICollectionView MoviesView { get; }
    public int[] SkeletonCards { get; } = Enumerable.Range(0, 12).ToArray();
    public ObservableCollection<Movie> Episodes { get; } = [];
    public ObservableCollection<int> Seasons { get; } = [];
    public ObservableCollection<WatchEntry> Recent { get; } = [];
    public ObservableCollection<WatchEntry> ContinueWatching { get; } = [];
    public ObservableCollection<Movie> FeaturedMovies { get; } = [];
    public ObservableCollection<Movie> LatestSeries { get; } = [];
    public ObservableCollection<Movie> HeroItems { get; } = [];
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
    public RelayCommand NextHeroCommand { get; }
    public RelayCommand PreviousHeroCommand { get; }
    public RelayCommand HeroDotCommand { get; }
    public string SearchText { get => searchText; set { if (Set(ref searchText, value)) DebounceSearch(); } }
    public string Heading { get => heading; private set => Set(ref heading, value); }
    public string Section => section;
    public bool IsHome => section == "home";
    public bool HasContinueWatching => ContinueWatching.Count > 0;
    public bool HasSeriesRow => LatestSeries.Count > 0;
    // The home banner rotates through the first titles; elsewhere it shows the selected title.
    public Movie? Hero => IsHome && HeroItems.Count > 0 && Selected is null ? HeroItems[Math.Clamp(heroIndex, 0, HeroItems.Count - 1)] : Selected ?? Movies.FirstOrDefault();
    public bool HasHero => Hero is not null;
    public int HeroIndex => heroIndex;
    public string HeroPositionLabel => HeroItems.Count > 0 && Selected is null ? $"{heroIndex + 1} / {HeroItems.Count}" : "";
    public string NextHeroLabel => HeroItems.Count > 1 && Selected is null ? "التالي: " + HeroItems[(heroIndex + 1) % HeroItems.Count].DisplayTitle + " ←" : "";
    /// <summary>The numbered "01–04" picks under the banner.</summary>
    public IEnumerable<Movie> Picks => FeaturedMovies.Skip(HeroItems.Count > 0 ? 1 : 0).Take(4).ToList();
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
    public Movie? Selected { get => selected; private set { Set(ref selected, value); Notify(nameof(HasSelection)); NotifyHero(); Notify(nameof(FavoriteLabel)); FavoriteCommand.Refresh(); WatchCommand.Refresh(); DownloadCommand.Refresh(); } }
    public bool HasSelection => Selected is not null;
    public string SelectedGenre { get => selectedGenre; set { if (Set(ref selectedGenre, value)) ApplyFilters(); } }
    public string SelectedYear { get => selectedYear; set { if (Set(ref selectedYear, value)) ApplyFilters(); } }
    public int SortIndex { get => sortIndex; set { if (Set(ref sortIndex, value)) ApplyFilters(); } }
    public int RatingIndex { get => ratingIndex; set { if (Set(ref ratingIndex, value)) ApplyFilters(); } }
    public int SelectedSeason { get => selectedSeason; set { if (Set(ref selectedSeason, value)) UpdateEpisodes(); } }
    public double CardWidth { get => settings.CardWidth; set => Change(settings with { CardWidth = value }); }
    public double TextSize { get => settings.TextSize; set => Change(settings with { TextSize = value }); }
    public bool CacheImages { get => settings.CacheImages; set => Change(settings with { CacheImages = value }); }
    public string UserName { get => settings.UserName; set { Change(settings with { UserName = value.Trim() }); Notify(nameof(DisplayName)); Notify(nameof(UserInitial)); } }
    public string DisplayName => settings.UserName.Length > 0 ? settings.UserName : "ضيف";
    public string UserInitial => DisplayName[..1];
    public int ParallelDownloads { get => settings.ParallelDownloads; set => Change(settings with { ParallelDownloads = Math.Clamp(value, 1, 3) }); }
    public int SpeedLimitKBps { get => settings.SpeedLimitKBps; set => Change(settings with { SpeedLimitKBps = Math.Max(0, value) }); }
    public bool AutoResumeDownloads { get => settings.AutoResumeDownloads; set => Change(settings with { AutoResumeDownloads = value }); }
    public bool NotifyOnComplete { get => settings.NotifyOnComplete; set => Change(settings with { NotifyOnComplete = value }); }
    private static readonly int[] SpeedLimits = [0, 512, 1024, 2048, 5120, 10240];
    public int ParallelIndex { get => ParallelDownloads - 1; set { ParallelDownloads = value + 1; Notify(); } }
    public int SpeedLimitIndex { get => Math.Max(0, Array.IndexOf(SpeedLimits, SpeedLimitKBps)); set { SpeedLimitKBps = SpeedLimits[Math.Clamp(value, 0, SpeedLimits.Length - 1)]; Notify(); } }
    public event Action? DownloadSettingsChanged;
    private void Change(AppSettings value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        // Re-read first: the player saves speed and subtitle size to the same file.
        var latest = Store.Load("settings", new AppSettings());
        settings = value with { SubtitleSize = latest.SubtitleSize, PlaybackSpeed = latest.PlaybackSpeed };
        Notify(name);
        try { Store.Save("settings", settings); } catch (Exception e) when (StorageError(e)) { Status = "تعذّر حفظ الإعدادات على القرص."; }
        if (name is nameof(ParallelDownloads) or nameof(SpeedLimitKBps)) DownloadSettingsChanged?.Invoke();
    }
    private static bool StorageError(Exception e) => e is IOException or UnauthorizedAccessException;
    private void RefreshCommands() { NextCommand?.Refresh(); PreviousCommand?.Refresh(); Notify(nameof(IsEmpty)); Notify(nameof(PageLabel)); }
    private void NotifyHero() { Notify(nameof(Hero)); Notify(nameof(HasHero)); Notify(nameof(HeroIndex)); Notify(nameof(HeroPositionLabel)); Notify(nameof(NextHeroLabel)); Notify(nameof(Picks)); }
    public void MoveHero(int step)
    {
        if (HeroItems.Count == 0 || Selected is not null) return;
        heroIndex = (heroIndex + step + HeroItems.Count) % HeroItems.Count; NotifyHero();
    }
    private async void DebounceSearch()
    {
        searching?.Cancel(); searching?.Dispose(); searching = new(); var token = searching.Token;
        if (SearchText.Trim().Length < 2) { Suggestions.Clear(); SuggestionsOpen = SearchText.Length == 0 && RecentSearches.Count > 0; return; }
        try
        {
            // Typing shows up to six matches under the box; Enter opens the full results page.
            await Task.Delay(300, token);
            var found = await service.Catalog("search", 0, SearchText.Trim(), false, false, token);
            token.ThrowIfCancellationRequested();
            Suggestions.Clear(); foreach (var m in found.Value.Take(6)) Suggestions.Add(m);
            SuggestionsOpen = Suggestions.Count > 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or InvalidDataException) { Log.Error("suggest", e); }
    }
    private async Task SearchNow()
    {
        searching?.Cancel();
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        query = SearchText.Trim(); SuggestionsOpen = false; RememberSearch(query);
        if (section != "search") { history.Push(section); BackCommand.Refresh(); }
        section = "search"; page = 0; ResetFilters(); await Load();
    }
    public void RefreshHistory()
    {
        var history = Store.History();
        Recent.Clear(); foreach (var e in history.Take(12)) Recent.Add(e);
        // "Continue watching": started but not finished.
        ContinueWatching.Clear(); foreach (var e in history.Where(e => e.Percent is > 2 and < 95).Take(12)) ContinueWatching.Add(e);
        Notify(nameof(HasContinueWatching));
    }
    public async Task Load(bool refresh = false)
    {
        loading?.Cancel(); loading?.Dispose(); loading = new(); var token = loading.Token;
        IsBusy = true; Status = "جارٍ تحميل العناوين…"; CanNext = false;
        // Clear the previous section immediately; failed navigation must not masquerade as new results.
        Movies.Clear(); current = [];
        selecting?.Cancel(); Selected = null; Episodes.Clear(); Seasons.Clear(); Notify(nameof(Section)); Notify(nameof(IsHome));
        if (!IsHome) { HeroItems.Clear(); FeaturedMovies.Clear(); LatestSeries.Clear(); Notify(nameof(HasSeriesRow)); }
        Heading = section switch { "home" => "أحدث الأفلام", "series" => "أحدث المسلسلات", "favorites" => "مفضلتك", "history" => "سجل المشاهدة", "library" => "مكتبتي • الملفات المنزّلة", "search" => $"نتائج «{query}»", _ => "أحدث الأفلام" };
        try
        {
            RefreshHistory();
            CacheResult<List<Movie>> result = section switch
            {
                "favorites" => new([.. favorites]),
                "history" => new(Store.History().Select(e => e.Movie with { ProgressText = e.ProgressLabel }).ToList()),
                "library" => new(LibraryProvider().OrderBy(m => m.GroupTitle).ThenBy(m => m.Season).ThenBy(m => m.Episode).ToList()),
                "home" => await service.Catalog("movies", page, "", refresh, false, token),
                _ => await service.Catalog(section, page, query, refresh, false, token)
            };
            token.ThrowIfCancellationRequested();
            current = result.Value; CanNext = section is not ("favorites" or "history" or "library") && current.Count >= 24;
            if (IsHome) await LoadHomeRows(refresh, token);
            var oldGenre = SelectedGenre; var oldYear = SelectedYear;
            Genres.Clear(); Genres.Add("الكل"); foreach (var g in current.SelectMany(m => m.Genres.Split('،', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct().Order()) Genres.Add(g);
            Years.Clear(); Years.Add("الكل"); foreach (var y in current.Select(m => m.Year).Where(y => y.Length > 0).Distinct().OrderDescending()) Years.Add(y);
            SelectedGenre = Genres.Contains(oldGenre) ? oldGenre : "الكل"; SelectedYear = Years.Contains(oldYear) ? oldYear : "الكل";
            ApplyFilters();
            if (!IsHome && section != "library" && Movies.FirstOrDefault() is { } first) _ = Select(first);
            string source = section is "favorites" or "history" or "library" ? "مكتبتك المحلية" : "سينمانا";
            Status = result.IsStale ? $"الاتصال غير متاح • نعرض نسخة محفوظة بتاريخ {result.SavedAt?.ToLocalTime():yyyy/MM/dd HH:mm}" : $"{current.Count} عنوانًا • {source} • الفلاتر والترتيب يطبّقان على الصفحة الحالية";
            if (section == "library" && current.Count == 0) Status = "لا توجد ملفات منزّلة مكتملة بعد. الملفات المكتملة تظهر هنا للمشاهدة بدون إنترنت.";
            if (CacheImages) _ = CachePosters(current.ToList(), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException || StorageError(e))
        { if (!token.IsCancellationRequested) Status = "تعذّر الاتصال بسينمانا. تحقق من الشبكة ثم اضغط إعادة المحاولة. يمكنك فتح المفضلة والسجل ومكتبتي دون اتصال."; }
        finally { if (!token.IsCancellationRequested) { IsBusy = false; Notify(nameof(IsEmpty)); RefreshCommands(); } }
    }
    private async Task LoadHomeRows(bool refresh, CancellationToken token)
    {
        HeroItems.Clear(); foreach (var m in current.Take(5)) HeroItems.Add(m);
        heroIndex = 0; NotifyHero();
        FeaturedMovies.Clear(); foreach (var m in current.Take(12)) FeaturedMovies.Add(m); Notify(nameof(Picks));
        LatestSeries.Clear();
        try
        {
            var series = await service.Catalog("series", 0, "", refresh, false, token);
            token.ThrowIfCancellationRequested();
            foreach (var m in series.Value.Take(12)) LatestSeries.Add(m);
        }
        catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException) { }
        Notify(nameof(HasSeriesRow));
        if (CacheImages) _ = CacheRow(HeroItems, token);
        if (CacheImages) _ = CacheRow(FeaturedMovies, token);
        if (CacheImages) _ = CacheRow(LatestSeries, token);
    }
    private async Task CacheRow(ObservableCollection<Movie> row, CancellationToken token)
    {
        try
        {
            var cached = await Task.WhenAll(row.ToList().Select(m => service.WithImage(m, token)));
            token.ThrowIfCancellationRequested();
            for (int i = 0; i < cached.Length && i < row.Count; i++) if (row[i].Id == cached[i].Id) row[i] = cached[i];
            if (ReferenceEquals(row, HeroItems)) NotifyHero();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (StorageError(e) || e is HttpRequestException) { }
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
        if (MoviesView is not null)
        {
            bool grouped = section == "library";
            if (grouped != MoviesView.GroupDescriptions.Count > 0)
            {
                MoviesView.GroupDescriptions.Clear();
                if (grouped) MoviesView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(Movie.GroupTitle)));
            }
        }
        Movies.Clear(); foreach (var movie in Filter(current, selectedGenre ?? "الكل", selectedYear ?? "الكل", RatingIndex == 0 ? 0 : RatingIndex + 5, SortIndex)) Movies.Add(movie);
        Notify(nameof(IsEmpty));
        NotifyHero();
    }
    private void ResetFilters() { selectedGenre = selectedYear = "الكل"; sortIndex = ratingIndex = 0; Notify(nameof(SelectedGenre)); Notify(nameof(SelectedYear)); Notify(nameof(SortIndex)); Notify(nameof(RatingIndex)); ApplyFilters(); }
    public async Task Select(Movie movie)
    {
        selecting?.Cancel(); selecting?.Dispose(); selecting = new(); var token = selecting.Token;
        Selected = movie; DetailsBusy = true; DetailsStatus = "جارٍ جلب التفاصيل…"; Episodes.Clear(); Seasons.Clear(); allEpisodes = [];
        try
        {
            var full = await service.Details(movie.Id, token);
            token.ThrowIfCancellationRequested(); Selected = full with { LocalPoster = movie.LocalPoster, LocalFile = movie.LocalFile };
            DetailsStatus = ""; NotifyContinue();
            if (full.Kind == "2")
            {
                allEpisodes = await service.Episodes(movie.Id, token); token.ThrowIfCancellationRequested();
                foreach (var season in allEpisodes.Select(m => m.Season).Distinct()) Seasons.Add(season);
                selectedSeason = 0; SelectedSeason = Seasons.FirstOrDefault(); UpdateEpisodes();
                if (allEpisodes.Count == 0) DetailsStatus = "تعذّر العثور على قائمة الحلقات لهذا العنوان حاليًا.";
                NotifyContinue();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        { if (!token.IsCancellationRequested) DetailsStatus = "تعذّر تحديث التفاصيل. يمكنك استخدام البيانات المتاحة أو إعادة اختيار العنوان."; }
        finally { if (!token.IsCancellationRequested) DetailsBusy = false; }
    }
    public void ClearSelection() { selecting?.Cancel(); Selected = null; Episodes.Clear(); Seasons.Clear(); allEpisodes = []; DetailsBusy = false; DetailsStatus = ""; NotifyContinue(); }
    private void NotifyContinue() { Notify(nameof(NextToWatch)); Notify(nameof(ContinueLabel)); ContinueCommand?.Refresh(); DownloadUnwatchedCommand?.Refresh(); }
    private void UpdateEpisodes()
    {
        var seen = Store.History().DistinctBy(h => h.Movie.Id).ToDictionary(h => h.Movie.Id, h => h.Percent);
        Episodes.Clear();
        foreach (var episode in allEpisodes.Where(m => m.Season == SelectedSeason))
            Episodes.Add(episode with { ProgressText = seen.TryGetValue(episode.Id, out var p) ? p >= 95 ? "✓ شوهدت" : $"{p:0}%" : "" });
    }
    private void ToggleFavorite()
    {
        if (Selected is null) return;
        var updated = favorites.Where(m => m.Id != Selected.Id).ToList(); if (updated.Count == favorites.Count) updated.Insert(0, Selected with { LocalFile = null });
        try { Store.Save("favorites", updated); favorites = updated; Notify(nameof(FavoriteLabel)); Notify(nameof(FavoriteCount)); if (section == "favorites") { loading?.Cancel(); current = [.. favorites]; ApplyFilters(); } }
        catch (Exception e) when (StorageError(e)) { Status = "تعذّر حفظ المفضلة. تحقق من المساحة وصلاحية الكتابة."; }
    }
    public void Dispose() { loading?.Cancel(); selecting?.Cancel(); searching?.Cancel(); service.Dispose(); loading?.Dispose(); selecting?.Dispose(); searching?.Dispose(); }
}
