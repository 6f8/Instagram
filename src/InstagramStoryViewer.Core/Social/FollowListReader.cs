using System.Net;
using System.Text.Json;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Json;
using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Social;

/// <summary>An HTTP answer obtained elsewhere (here: fetched inside the logged-in instagram.com page).</summary>
public sealed record PageResult(int Status, string Body);

/// <summary>A whole follow list and how many requests reading it took.</summary>
public sealed record FollowListResult(List<StoryUser> Users, int Requests);

/// <summary>The list is longer than the requests the caller allowed; nothing partial is returned.</summary>
public sealed class PageBudgetExceededException(int budget)
    : Exception($"The follow list needs more than {budget} requests.")
{
    public int Budget { get; } = budget;
}

/// <summary>
/// Reads a whole follow list page by page. The pages are fetched by the caller — the app fetches them from
/// inside the embedded browser, exactly like instagram.com does when the followers dialog is scrolled, because
/// Instagram answers the same requests from outside the browser with "please wait a few minutes".
/// </summary>
public static class FollowListReader
{
    /// <summary>Accounts per page: what instagram.com itself asks for.</summary>
    public const int PageSize = 12;

    private const int MaxPages = 5000; // 60,000 accounts: just a guard against a cursor that never ends

    public static string PagePath(string userId, bool followers, string? cursor) =>
        $"/api/v1/friendships/{Uri.EscapeDataString(userId)}/{(followers ? "followers" : "following")}/?count={PageSize}"
        + (followers ? "&search_surface=follow_list_page" : "")
        + (cursor is null ? "" : "&max_id=" + Uri.EscapeDataString(cursor));

    /// <summary>A random pause between pages, like a person scrolling the list.</summary>
    public static TimeSpan Pause() => TimeSpan.FromMilliseconds(1500 + Random.Shared.Next(0, 2000));

    /// <param name="fetch">Fetches a path (see <see cref="PagePath"/>) and returns the raw answer.</param>
    /// <param name="progress">Number of accounts read so far.</param>
    /// <param name="maxRequests">Requests the caller allows; a longer list throws <see cref="PageBudgetExceededException"/>.</param>
    public static async Task<FollowListResult> ReadAllAsync(string userId, bool followers,
        Func<string, CancellationToken, Task<PageResult>> fetch, Func<TimeSpan, CancellationToken, Task> delay,
        IProgress<int>? progress, CancellationToken ct, int maxRequests = MaxPages)
    {
        var users = new List<StoryUser>();
        var seen = new HashSet<string>();
        string? cursor = null;
        var budget = Math.Min(maxRequests, MaxPages);
        for (var page = 0; ; page++)
        {
            if (page >= budget) throw new PageBudgetExceededException(maxRequests);
            if (page > 0) await delay(Pause(), ct).ConfigureAwait(false);
            var path = PagePath(userId, followers, cursor);
            var res = Parse<FriendshipListResponse>(await fetch(path, ct).ConfigureAwait(false), path);
            foreach (var u in res.Users ?? [])
                if (u.Pk is not null && seen.Add(u.Pk)) users.Add(u);
            progress?.Report(users.Count);
            if (string.IsNullOrEmpty(res.NextMaxId) || res.NextMaxId == cursor || res.Users is not { Count: > 0 })
                return new FollowListResult(users, page + 1);
            cursor = res.NextMaxId;
        }
    }

    /// <summary>Turns an answer into data, or into the same error kinds the API client uses.</summary>
    public static T Parse<T>(PageResult result, string path) where T : ApiResponseBase
    {
        T? parsed = null;
        try
        {
            if (result.Body.TrimStart().StartsWith('{'))
                parsed = (T?)JsonSerializer.Deserialize(result.Body, typeof(T), ApiJsonContext.Default);
        }
        catch (JsonException)
        {
            // classified below as an invalid answer
        }

        var msg = parsed?.Message ?? "";
        InstagramApiException Fail(InstagramErrorKind kind, string text) =>
            new(kind, text, (HttpStatusCode)result.Status) { ResponseBody = Truncate(result.Body), RequestPath = path };

        if (result.Status == 429 || msg.Contains("wait a few minutes", StringComparison.OrdinalIgnoreCase) || parsed?.Spam == true)
            throw Fail(InstagramErrorKind.RateLimited, "طلب إنستجرام التمهّل قبل قراءة المزيد من القائمة.");
        if (msg.Contains("checkpoint", StringComparison.OrdinalIgnoreCase) || parsed?.CheckpointUrl is not null)
            throw Fail(InstagramErrorKind.Checkpoint, "إنستجرام يطلب التحقق من الحساب. افتح إنستجرام في المتصفح وأكمل التحقق.");
        if (result.Status == 401 || parsed?.RequireLogin == true || msg.Contains("login_required", StringComparison.OrdinalIgnoreCase))
            throw Fail(InstagramErrorKind.LoginRequired, "المتصفح المدمج غير مسجّل الدخول.");
        if (parsed is null)
            throw Fail(InstagramErrorKind.InvalidResponse, $"أرسل إنستجرام ردّاً غير متوقع (HTTP {result.Status}).");
        if (result.Status is < 200 or >= 300 || string.Equals(parsed.Status, "fail", StringComparison.OrdinalIgnoreCase))
            throw Fail(InstagramErrorKind.Unknown, $"خطأ من إنستجرام (HTTP {result.Status}): {msg}");
        return parsed;
    }

    private static string Truncate(string s) => s.Length > 64 * 1024 ? s[..(64 * 1024)] : s;
}
