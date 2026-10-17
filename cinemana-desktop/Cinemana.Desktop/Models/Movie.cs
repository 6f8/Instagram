using System.Globalization;
using System.Text.Json;

namespace Cinemana.Desktop.Models;

public record Movie(string Id, string Title, string Description, string Poster, string Year, string Rating)
{
    public string ArabicTitle { get; init; } = "";
    public string Genres { get; init; } = "";
    public string Language { get; init; } = "";
    public string Cast { get; init; } = "";
    public string Director { get; init; } = "";
    public string Kind { get; init; } = "1";
    public int Season { get; init; }
    public int Episode { get; init; }
    public double Duration { get; init; }
    /// <summary>The service lists at least one subtitle file (known after the details request).</summary>
    public bool HasSubtitles { get; init; }
    public string? LocalPoster { get; init; }
    /// <summary>Set for titles in "My library": the downloaded video to play offline.</summary>
    public string? LocalFile { get; init; }
    // "My library" only (not saved): when the file was added, its size and how much was watched.
    [System.Text.Json.Serialization.JsonIgnore] public DateTime AddedAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public long SizeBytes { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public double WatchedPercent { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsLocal => LocalFile is not null;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsRemote => LocalFile is null;
    /// <summary>Group name in "My library": the series (or film) title.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string GroupTitle => DisplayTitle;
    public string ProgressText { get; init; } = "";
    public string ImageSource => LocalPoster ?? Poster;
    public string DisplayTitle => string.IsNullOrWhiteSpace(ArabicTitle) ? Title : ArabicTitle;
    public string Caption => $"{Year}  •  ★ {Rating}";
    [System.Text.Json.Serialization.JsonIgnore] public string[] CastNames => Cast.Split('،', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(8).ToArray();
    public string FirstGenre => Genres.Split('،', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    public string HoursLabel => Duration > 0 ? (Duration >= 3600 ? $"{(int)(Duration / 3600)} س {(int)(Duration % 3600 / 60)} د" : $"{(int)(Duration / 60)} د") : "";
    // Card subtitle as in the design: year • genre • duration (missing parts are skipped).
    public string MetaLine => string.Join(" • ", new[] { Year, FirstGenre, HoursLabel }.Where(x => x.Length > 0));
    public string TypeLabel => Kind == "2" ? "مسلسل" : "فيلم";
    public string DurationLabel => Duration > 0 ? $"{(int)(Duration / 60)} دقيقة" : "";
    public string EpisodeLabel => $"الموسم {Season} • الحلقة {Episode}";
    public double NumericRating => double.TryParse(Rating, CultureInfo.InvariantCulture, out var value) ? value : 0;
    public static Movie Parse(JsonElement e)
    {
        string Get(string key) => e.TryGetProperty(key, out var v) ? v.ToString() : "";
        string Names(string key, string field) => e.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
            ? string.Join("، ", a.EnumerateArray().Select(x => x.TryGetProperty(field, out var n) ? n.ToString() : "").Where(s => s.Length > 0)) : "";
        string Pick(string a, string b) => Get(a) is { Length: > 0 } v ? v : Get(b);
        int Number(string key) => int.TryParse(Get(key), out var n) ? n : 0;
        double.TryParse(Get("duration"), CultureInfo.InvariantCulture, out var duration);
        return new(Get("nb"), Pick("en_title", "ar_title"), Pick("ar_content", "en_content"),
            Pick("imgMediumThumbObjUrl", "imgObjUrl"), Get("year"), Get("stars"))
        {
            ArabicTitle = Get("ar_title"),
            Genres = Names("categories", "ar_title"),
            Cast = Names("actorsInfo", "name"),
            Director = Names("directorsInfo", "name"),
            Kind = Get("kind"),
            Season = Number("season"),
            Episode = Number("episodeNummer"),
            HasSubtitles = e.TryGetProperty("translations", out var tr) && tr.ValueKind == JsonValueKind.Array && tr.GetArrayLength() > 0
                || Get("arTranslationFilePath").Length > 0 || Get("enTranslationFilePath").Length > 0,
            Duration = duration,
            Language = e.TryGetProperty("videoLanguages", out var language) && language.ValueKind == JsonValueKind.Object && language.TryGetProperty("ar_title", out var name) ? name.ToString() : ""
        };
    }
}

public record WatchEntry(Movie Movie, DateTimeOffset OpenedAt, double Position = 0, double Duration = 0)
{
    public string ProgressLabel => Position > 0 ? $"{TimeSpan.FromSeconds(Position):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(Duration):hh\\:mm\\:ss}" : "تم فتح صفحة المشاهدة";
    public double Percent => Duration > 0 ? Math.Clamp(Position / Duration * 100, 0, 100) : 0;
}
public record AppSettings(bool CacheImages = true, double CardWidth = 174, double TextSize = 14)
{
    public string UserName { get; init; } = "";
    public int ParallelDownloads { get; init; } = 2;
    public int SpeedLimitKBps { get; init; } // 0 = unlimited
    public bool AutoResumeDownloads { get; init; }
    public string LastSection { get; init; } = "home";
    public List<string> RecentSearches { get; init; } = [];
    public bool NotifyOnComplete { get; init; } = true;
    // Downloads: schedule window (hours, local), night limit, metered pause, action after the queue
    public bool ScheduleEnabled { get; init; }
    public int ScheduleStart { get; init; } = 2;
    public int ScheduleEnd { get; init; } = 8;
    public int NightSpeedLimitKBps { get; init; } = -1; // -1 = same as day
    public bool PauseOnMetered { get; init; } = true;
    public int AfterQueue { get; init; } // 0 nothing, 1 sleep, 2 shut down
    public bool AutoDownloadFollowed { get; init; } = true;
    // Library
    public int AutoDeleteWatchedDays { get; init; } // 0 = never
    public double SubtitleSize { get; init; } = 27;
    public double PlaybackSpeed { get; init; } = 1;
    // Player
    public bool AutoNext { get; init; } = true;
    public double Volume { get; init; } = 0.8;
    public bool Muted { get; init; }
    public string SubtitleFont { get; init; } = "Segoe UI";
    public string SubtitleColor { get; init; } = "#FFFFFF";
    public double SubtitleBackground { get; init; } = 0.6;
    public bool SubtitleTop { get; init; }
}
/// <summary>Personal note and 0–5 stars for a title.</summary>
public record Note(string Text = "", int Stars = 0);
public record CacheResult<T>(T Value, bool IsStale = false, DateTimeOffset? SavedAt = null);
