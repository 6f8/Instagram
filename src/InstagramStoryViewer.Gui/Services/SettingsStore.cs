using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstagramStoryViewer.Automation;

namespace InstagramStoryViewer.Gui.Services;

public sealed class AppSettings
{
    /// <summary>Layout version of this file; <see cref="SettingsMigrations"/> upgrades older files on load.</summary>
    public int SchemaVersion { get; set; }

    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Instagram Stories");

    public bool ConfirmBeforeMarkSeen { get; set; } = true;
    public bool AutoPlayVideos { get; set; } = true;

    /// <summary>The cookie header, encrypted with Windows DPAPI (readable only by this Windows user).</summary>
    public string? ProtectedSession { get; set; }

    /// <summary>Set by logout so the embedded browser does not silently log back in.</summary>
    public bool ClearBrowserOnNextLogin { get; set; }

    /// <summary>The user acknowledged the terms-of-use notice shown on first start.</summary>
    public bool AcceptedDisclaimer { get; set; }

    /// <summary>Safety cap on Instagram API calls per hour.</summary>
    public int MaxRequestsPerHour { get; set; } = 180;

    /// <summary>Watch a story automatically when it is selected in the table.</summary>
    public bool AutoWatchOnPreview { get; set; }

    /// <summary>Watch new stories of followed accounts in the background.</summary>
    public bool AutoWatchNew { get; set; }

    public int AutoWatchDailyLimit { get; set; } = 200;

    /// <summary>Like every story the app watches (counted in the daily like limit).</summary>
    public bool LikeWhileWatching { get; set; } = true;

    /// <summary>Stories watched automatically today.</summary>
    public DailyCount AutoWatchCount { get; set; } = new();

    // Schema 0 kept the daily counters as loose fields; read once by the migration, never written again.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AutoWatchDay { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? AutoWatchedToday { get; set; }

    /// <summary>Accounts that are never watched automatically (manual watching asks first).</summary>
    public List<AccountRef> ExcludedFromWatch { get; set; } = new();

    /// <summary>Starred accounts: shown first, optionally auto-downloaded and announced.</summary>
    public List<AccountRef> Favorites { get; set; } = new();

    public bool AutoDownloadFavorites { get; set; }
    public bool NotifyFavorites { get; set; } = true;

    /// <summary>latest_reel_media last announced per favorite, so each new story is announced once.</summary>
    public Dictionary<string, long> FavoriteLastNotified { get; set; } = new();

    /// <summary>No automatic watching between these local hours (wraps past midnight).</summary>
    public bool QuietHoursEnabled { get; set; } = true;
    public int QuietFrom { get; set; } = 1;
    public int QuietTo { get; set; } = 8;

    /// <summary>Closing the window keeps the app running in the notification area.</summary>
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }

    public bool CheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>Like new stories/posts automatically (in the background, after each refresh).</summary>
    public bool AutoLikeEnabled { get; set; }
    public bool AutoLikeStories { get; set; } = true;
    public bool AutoLikePosts { get; set; } = true;

    /// <summary>"Favorites" or "All" (all followed accounts; posts are always favorites only).</summary>
    public string AutoLikeScope { get; set; } = "Favorites";

    public int AutoLikeDailyLimit { get; set; } = 60;

    /// <summary>Likes given automatically today.</summary>
    public DailyCount AutoLikeCount { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AutoLikeDay { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? AutoLikedToday { get; set; }

    /// <summary>Schema 0 only: handled media ids, now kept in the history database.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? AutoLikeHandled { get; set; }

    /// <summary>Round-robin position over favorites for the posts check.</summary>
    public int AutoLikePostCursor { get; set; }

    /// <summary>Accounts the user follows on purpose without expecting a follow back (hidden in "Not following back").</summary>
    public List<AccountRef> FollowBackIgnored { get; set; } = new();

    /// <summary>Main window position and size, restored on the next start.</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>Last time the page selectors were refreshed from GitHub.</summary>
    public DateTime? LastSelectorsCheck { get; set; }
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Upgrades settings written by older versions.</summary>
public static class SettingsMigrations
{
    public const int Current = 1;

    /// <summary>
    /// Returns the handled media ids found in an old file (to move into the history database), or null.
    /// The settings object is upgraded in place.
    /// </summary>
    public static List<string>? Upgrade(AppSettings s)
    {
        List<string>? handled = null;
        if (s.SchemaVersion < 1)
        {
            // 0 → 1: loose daily counters become DailyCount objects; the handled list leaves settings.json.
            if (s.AutoWatchDay is not null)
                s.AutoWatchCount = new DailyCount { Day = s.AutoWatchDay, Count = s.AutoWatchedToday ?? 0 };
            if (s.AutoLikeDay is not null)
                s.AutoLikeCount = new DailyCount { Day = s.AutoLikeDay, Count = s.AutoLikedToday ?? 0 };
            handled = s.AutoLikeHandled;
            s.AutoWatchDay = s.AutoLikeDay = null;
            s.AutoWatchedToday = s.AutoLikedToday = null;
            s.AutoLikeHandled = null;
        }
        s.SchemaVersion = Current;
        return handled;
    }
}

/// <summary>An Instagram account remembered by the app (username can change; the id cannot).</summary>
public sealed class AccountRef
{
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
}

/// <summary>Persists settings to %AppData%\InstagramStoryViewer\settings.json.</summary>
public static class SettingsStore
{
    public static string AppDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InstagramStoryViewer");

    public static string WebViewDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InstagramStoryViewer", "WebView2");

    private static string FilePath => Path.Combine(AppDataFolder, "settings.json");
    private static string BackupPath => FilePath + ".bak";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static AppSettings Load()
    {
        // Fall back to the backup when the main file is missing or corrupt (e.g. a crash mid-write in an
        // older version), so a damaged file never silently throws away the saved session.
        return TryRead(FilePath) ?? TryRead(BackupPath) ?? new AppSettings();
    }

    private static AppSettings? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes atomically: the new content goes to a temp file which then replaces settings.json in one
    /// step (keeping the previous version as .bak). A crash can no longer leave a half-written file.
    /// </summary>
    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(AppDataFolder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, BackupPath, ignoreMetadataErrors: true);
            else
                File.Move(tmp, FilePath);
        }
    }

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
