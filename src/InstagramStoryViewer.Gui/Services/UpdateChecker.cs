using System.Net.Http.Headers;
using System.Text.Json;

namespace InstagramStoryViewer.Gui.Services;

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl);

/// <summary>Asks GitHub for the latest release of this project.</summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/6f8/Instagram/releases/latest";
    public const string ReleasesPage = "https://github.com/6f8/Instagram/releases/latest";

    /// <summary>The newer release, or null when up to date / offline / no release yet.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("InstagramStoryViewer", current.ToString(3)));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi, ct));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
            return Version.TryParse(tag.TrimStart('v', 'V'), out var latest) && latest > current
                ? new UpdateInfo(latest, tag, url)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
