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
}

/// <summary>Persists settings to %AppData%\InstagramStoryViewer\settings.json.</summary>
public static class SettingsStore
{
    public static string AppDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InstagramStoryViewer");

    public static string WebViewDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InstagramStoryViewer", "WebView2");

    private static string FilePath => Path.Combine(AppDataFolder, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable file falls back to defaults instead of crashing on startup.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataFolder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
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
