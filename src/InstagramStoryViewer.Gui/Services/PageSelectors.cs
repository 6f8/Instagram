using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InstagramStoryViewer.Diagnostics;

namespace InstagramStoryViewer.Gui.Services;

/// <summary>
/// What the embedded-browser scripts look for on Instagram's page (button labels).
/// Instagram changes its page often; keeping these as data means a fix is a one-line change to
/// config/selectors.json in the repository, picked up by every copy of the app within a day, with no new
/// release. Only data is downloaded, never script code, and every pattern must compile before it is used.
/// </summary>
public sealed class PageSelectors
{
    public const string RemoteUrl = "https://raw.githubusercontent.com/6f8/Instagram/HEAD/config/selectors.json";
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromDays(1);

    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("viewStory")] public string ViewStory { get; set; } = "";
    [JsonPropertyName("liked")] public string Liked { get; set; } = "";
    [JsonPropertyName("like")] public string Like { get; set; } = "";

    /// <summary>The selectors as a JSON object literal, ready to pass into a page script.</summary>
    public string ToScriptArgument() => JsonSerializer.Serialize(this);

    private static string LocalOverride => Path.Combine(SettingsStore.AppDataFolder, "selectors.json");
    private static string RemoteCopy => Path.Combine(SettingsStore.AppDataFolder, "selectors.remote.json");

    /// <summary>
    /// The newest valid set: a hand-edited %AppData% file wins, then the copy last fetched from GitHub, then
    /// the defaults built into the exe.
    /// </summary>
    public static PageSelectors Load()
    {
        var builtIn = ReadBuiltIn();
        var best = builtIn;
        foreach (var path in new[] { RemoteCopy, LocalOverride })
        {
            if (TryRead(path) is { } candidate && (path == LocalOverride || candidate.Version >= best.Version))
                best = candidate;
        }
        return best;
    }

    /// <summary>Fetches the repository copy (at most once a day). Returns the updated set, or null when unchanged.</summary>
    public static async Task<PageSelectors?> RefreshAsync(AppSettings settings, PageSelectors current, CancellationToken ct = default)
    {
        if (settings.LastSelectorsCheck is { } last && DateTime.UtcNow - last < RefreshEvery) return null;
        settings.LastSelectorsCheck = DateTime.UtcNow;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("InstagramStoryViewer", "1"));
            var json = await http.GetStringAsync(RemoteUrl, ct);
            var remote = Parse(json);
            if (remote is null || remote.Version <= current.Version) return null;
            Directory.CreateDirectory(SettingsStore.AppDataFolder);
            await File.WriteAllTextAsync(RemoteCopy, json, ct);
            AppLog.Info($"Page selectors updated to version {remote.Version}");
            return remote;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Selector refresh skipped: {ex.Message}");
            return null;
        }
    }

    private static PageSelectors ReadBuiltIn()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("selectors.json")
            ?? throw new InvalidOperationException("selectors.json is missing from the build");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd()) ?? throw new InvalidOperationException("built-in selectors.json is invalid");
    }

    private static PageSelectors? TryRead(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Parses and validates a selectors file; anything incomplete or not compiling is rejected.</summary>
    public static PageSelectors? Parse(string json)
    {
        try
        {
            var s = JsonSerializer.Deserialize<PageSelectors>(json);
            if (s is null) return null;
            string[] patterns = [s.ViewStory, s.Liked, s.Like];
            if (patterns.Any(string.IsNullOrWhiteSpace)) return null;
            foreach (var p in patterns) _ = new Regex(p, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            return s;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            AppLog.Warn($"Ignoring invalid selectors file: {ex.Message}");
            return null;
        }
    }
}
