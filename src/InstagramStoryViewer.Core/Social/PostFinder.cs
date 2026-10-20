using System.Text.Json;

namespace InstagramStoryViewer.Social;

/// <summary>A post found by a search, enough to show it and to comment on it.</summary>
public sealed record PostSummary(string Pk, string Code, string Username, string? Caption, string? ThumbnailUrl,
    long TakenAt, int Likes, int Comments, bool IsVideo, string? UserPk = null)
{
    public string Url => $"https://www.instagram.com/p/{Code}/";
    public DateTimeOffset TakenAtTime => DateTimeOffset.FromUnixTimeSeconds(TakenAt);
}

/// <summary>
/// Finds posts in any Instagram answer (hashtag pages, search results, feeds). Their layouts change often and
/// nest posts at different depths, so instead of one fixed shape every object that looks like a post
/// (a "code" plus "taken_at") is collected, wherever it is.
/// </summary>
public static class PostFinder
{
    /// <summary>Hashtag page as the website loads it.</summary>
    public static string HashtagPath(string tag) => $"/api/v1/tags/web_info/?tag_name={Uri.EscapeDataString(NormalizeTag(tag))}";

    /// <summary>The website's search results page (fallback for hashtags).</summary>
    public static string SearchPath(string query) => $"/api/v1/fbsearch/web/top_serp/?query={Uri.EscapeDataString(query)}";

    public static string NormalizeTag(string tag) => tag.Trim().TrimStart('#').Replace(" ", "", StringComparison.Ordinal);

    /// <summary>Posts in the answer, newest first, each once.</summary>
    public static List<PostSummary> Extract(string json)
    {
        var found = new Dictionary<string, PostSummary>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            Walk(doc.RootElement, found, 0);
        }
        catch (JsonException)
        {
            // not JSON: no posts
        }
        return found.Values.OrderByDescending(p => p.TakenAt).ToList();
    }

    private static void Walk(JsonElement e, Dictionary<string, PostSummary> found, int depth)
    {
        if (depth > 40) return;
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray()) Walk(x, found, depth + 1);
            return;
        }
        if (e.ValueKind != JsonValueKind.Object) return;
        if (TryRead(e) is { } post && !found.ContainsKey(post.Code)) found[post.Code] = post;
        foreach (var prop in e.EnumerateObject()) Walk(prop.Value, found, depth + 1);
    }

    private static PostSummary? TryRead(JsonElement e)
    {
        if (!e.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String) return null;
        if (!e.TryGetProperty("taken_at", out var taken)) return null;
        var takenAt = taken.ValueKind == JsonValueKind.Number ? taken.GetInt64()
            : long.TryParse(taken.GetString(), out var t) ? t : 0;
        var pk = Str(e, "pk") ?? Str(e, "id") ?? code.GetString()!;
        var hasUser = e.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object;
        var user = hasUser ? Str(u, "username") : null;
        var userPk = hasUser ? Str(u, "pk") ?? Str(u, "id") : null;
        string? caption = null;
        if (e.TryGetProperty("caption", out var c))
            caption = c.ValueKind == JsonValueKind.Object ? Str(c, "text") : c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        return new PostSummary(pk, code.GetString()!, user ?? "", caption, Thumbnail(e), takenAt,
            Int(e, "like_count"), Int(e, "comment_count"), Int(e, "media_type") == 2, userPk);
    }

    /// <summary>The smallest image at least 150 px wide (carousels: the first item).</summary>
    private static string? Thumbnail(JsonElement e)
    {
        if (!e.TryGetProperty("image_versions2", out var iv) &&
            !(e.TryGetProperty("carousel_media", out var cm) && cm.ValueKind == JsonValueKind.Array && cm.GetArrayLength() > 0
              && cm[0].TryGetProperty("image_versions2", out iv)))
            return null;
        if (!iv.TryGetProperty("candidates", out var cands) || cands.ValueKind != JsonValueKind.Array) return null;
        return cands.EnumerateArray()
            .Select(x => (Url: Str(x, "url"), W: Int(x, "width")))
            .Where(x => x.Url is not null)
            .OrderBy(x => x.W < 150 ? int.MaxValue : x.W)
            .Select(x => x.Url)
            .FirstOrDefault();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        } : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
