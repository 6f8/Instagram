using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InstagramStoryViewer.Gui.Services;

public sealed class AppSettings
{
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

    /// <summary>Local date (yyyy-MM-dd) that <see cref="AutoWatchedToday"/> counts for.</summary>
    public string? AutoWatchDay { get; set; }

    public int AutoWatchedToday { get; set; }
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
