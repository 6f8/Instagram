using System.Net.Http;
using System.Text.Json;

namespace Cinemana.Desktop.Services;

/// <summary>Looks for a newer "cinemana-v*" GitHub release. Never downloads or installs anything by itself.</summary>
public static class UpdateCheck
{
    public const string ReleasesPage = "https://github.com/6f8/Instagram/releases";
    public sealed record Update(Version Version, string Url, string Notes);

    public static async Task<Update?> Find(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Cinemana-Desktop/" + Log.Version);
            using var doc = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/6f8/Instagram/releases?per_page=20", ct));
            var current = Version.Parse(Log.Version);
            Update? best = null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                string tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                if (!tag.StartsWith("cinemana-v", StringComparison.Ordinal) || release.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                if (!Version.TryParse(tag["cinemana-v".Length..], out var version) || version <= current || best is not null && version <= best.Version) continue;
                string url = release.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
                string notes = release.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                best = new(version, url, notes);
            }
            return best;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or JsonException or FormatException or ArgumentException)
        { Log.Error("update-check", e); return null; }
    }
}
