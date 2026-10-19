using System.Text.Json;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Social;

/// <summary>
/// Reads a follow list through the website's GraphQL query (50 accounts per request). Unlike the friendships
/// endpoint, it reports the list's total count, so an incomplete read is detected instead of silently
/// turning missing followers into "not following back".
/// </summary>
public static class GraphFollowReader
{
    public const int PageSize = 50;
    private const string FollowersHash = "c76146de99bb02f6415203be841dd25a";
    private const string FollowingHash = "d04b0a864b4b54837c0d870b0e77e076";

    public sealed record GraphList(List<StoryUser> Users, int Total, int Requests);

    public static string PagePath(string userId, bool followers, string? after)
    {
        var vars = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = userId, ["include_reel"] = false, ["fetch_mutual"] = false, ["first"] = PageSize, ["after"] = after
        });
        return $"/graphql/query/?query_hash={(followers ? FollowersHash : FollowingHash)}&variables={Uri.EscapeDataString(vars)}";
    }

    /// <remarks>Continues on the thread pool after its awaits; <paramref name="fetch"/> must dispatch itself if needed.</remarks>
    public static async Task<GraphList> ReadAllAsync(string userId, bool followers,
        Func<string, CancellationToken, Task<PageResult>> fetch, Func<TimeSpan, CancellationToken, Task> delay,
        IProgress<int>? progress, CancellationToken ct, int maxRequests)
    {
        var users = new List<StoryUser>();
        var seen = new HashSet<string>();
        string? after = null;
        var total = 0;
        for (var page = 0; ; page++)
        {
            if (page >= maxRequests) throw new PageBudgetExceededException(maxRequests);
            if (page > 0) await delay(FollowListReader.Pause(), ct).ConfigureAwait(false);
            var path = PagePath(userId, followers, after);
            var res = await fetch(path, ct).ConfigureAwait(false);
            var (pageUsers, count, hasNext, cursor) = Parse(res, path, followers);
            total = count;
            foreach (var u in pageUsers)
                if (u.Pk is not null && seen.Add(u.Pk)) users.Add(u);
            progress?.Report(users.Count);
            if (!hasNext || string.IsNullOrEmpty(cursor) || cursor == after) return new GraphList(users, total, page + 1);
            after = cursor;
        }
    }

    internal static (List<StoryUser> Users, int Count, bool HasNext, string? Cursor) Parse(PageResult res, string path, bool followers)
    {
        // Errors (429, login, "wait a few minutes") are classified the same way as for the other endpoints.
        if (res.Status is < 200 or >= 300 || !res.Body.TrimStart().StartsWith('{'))
            FollowListReader.Parse<ApiResponseBase>(res, path);
        try
        {
            using var doc = JsonDocument.Parse(res.Body);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var m) && m.GetString() is { } msg && msg.Contains("wait", StringComparison.OrdinalIgnoreCase))
                throw new InstagramApiException(InstagramErrorKind.RateLimited, "طلب إنستجرام التمهّل.") { RequestPath = path };
            var edge = root.GetProperty("data").GetProperty("user").GetProperty(followers ? "edge_followed_by" : "edge_follow");
            var info = edge.GetProperty("page_info");
            var list = new List<StoryUser>();
            foreach (var e in edge.GetProperty("edges").EnumerateArray())
            {
                var n = e.GetProperty("node");
                list.Add(new StoryUser
                {
                    Pk = n.GetProperty("id").GetString(),
                    Username = n.TryGetProperty("username", out var un) ? un.GetString() : null,
                    FullName = n.TryGetProperty("full_name", out var fn) ? fn.GetString() : null,
                    ProfilePicUrl = n.TryGetProperty("profile_pic_url", out var pp) ? pp.GetString() : null,
                    IsVerified = n.TryGetProperty("is_verified", out var v) && v.ValueKind == JsonValueKind.True,
                    IsPrivate = n.TryGetProperty("is_private", out var p) && p.ValueKind == JsonValueKind.True,
                });
            }
            return (list, edge.TryGetProperty("count", out var c) && c.TryGetInt32(out var ci) ? ci : 0,
                info.TryGetProperty("has_next_page", out var h) && h.ValueKind == JsonValueKind.True,
                info.TryGetProperty("end_cursor", out var ec) && ec.ValueKind == JsonValueKind.String ? ec.GetString() : null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InstagramApiException(InstagramErrorKind.InvalidResponse, "ردّ غير متوقع من استعلام القائمة.", inner: ex)
            { RequestPath = path, ResponseBody = res.Body.Length > 4000 ? res.Body[..4000] : res.Body };
        }
    }
}
