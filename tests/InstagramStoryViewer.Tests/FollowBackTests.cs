using InstagramStoryViewer.Api;
using InstagramStoryViewer.Models;
using InstagramStoryViewer.Social;

namespace InstagramStoryViewer.Tests;

public class FollowBackTests
{
    private static StoryUser U(string pk) => new() { Pk = pk, Username = "u" + pk };
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    [Fact]
    public void Not_following_back_is_following_minus_followers()
    {
        var result = FollowBack.NotFollowingBack([U("1"), U("2"), U("3"), U("4")], [U("2"), U("4"), U("9")]);
        Assert.Equal(["1", "3"], result.Select(u => u.Pk));
    }

    [Fact]
    public async Task Reads_every_page_of_a_follow_list()
    {
        var paths = new List<string>();
        Task<PageResult> Fetch(string path, CancellationToken _)
        {
            paths.Add(path);
            return Task.FromResult(new PageResult(200, path.Contains("max_id=p2")
                ? """{"users":[{"pk":3,"username":"c"},{"pk":"2","username":"b"}],"next_max_id":null,"status":"ok"}"""
                : """{"users":[{"pk":"1","username":"a","is_verified":true},{"pk":"2","username":"b"}],"next_max_id":"p2","status":"ok"}"""));
        }
        var progress = new List<int>();
        var result = await FollowListReader.ReadAllAsync("123", followers: false, Fetch, NoDelay, new SyncProgress(progress.Add), default);
        var users = result.Users;
        Assert.Equal(2, result.Requests);

        Assert.Equal(["1", "2", "3"], users.Select(u => u.Pk)); // duplicate across pages kept once
        Assert.True(users[0].IsVerified);
        Assert.Equal(2, paths.Count);
        Assert.StartsWith("/api/v1/friendships/123/following/?count=12", paths[0]);
        Assert.Equal([2, 3], progress);
    }

    [Fact]
    public async Task Followers_use_the_follow_list_surface_and_stop_on_a_repeated_cursor()
    {
        var paths = new List<string>();
        var users = await FollowListReader.ReadAllAsync("123", followers: true, (path, _) =>
        {
            paths.Add(path);
            return Task.FromResult(new PageResult(200, """{"users":[{"pk":"1"}],"next_max_id":"same","status":"ok"}"""));
        }, NoDelay, null, default);
        Assert.Equal(2, paths.Count); // first page, then the cursor repeats → stop
        Assert.Contains("search_surface=follow_list_page", paths[0]);
        Assert.Single(users.Users);
    }

    [Fact]
    public async Task A_list_longer_than_the_budget_stops_without_a_partial_result()
    {
        var calls = 0;
        await Assert.ThrowsAsync<PageBudgetExceededException>(() => FollowListReader.ReadAllAsync("1", false, (_, _) =>
        {
            calls++;
            return Task.FromResult(new PageResult(200, $$"""{"users":[{"pk":"{{calls}}"}],"next_max_id":"c{{calls}}","status":"ok"}"""));
        }, NoDelay, null, default, maxRequests: 3));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Rate_limit_with_an_empty_body_is_recognised()
    {
        // What Instagram actually sent: HTTP 429, no content type, no body.
        var ex = await Assert.ThrowsAsync<InstagramApiException>(() => FollowListReader.ReadAllAsync("1", true,
            (_, _) => Task.FromResult(new PageResult(429, "")), NoDelay, null, default));
        Assert.Equal(InstagramErrorKind.RateLimited, ex.Kind);
    }

    [Theory]
    [InlineData(429, """{"message":"rate limited","status":"fail"}""", InstagramErrorKind.RateLimited)]
    [InlineData(401, """{"message":"Please wait a few minutes before you try again.","require_login":true,"status":"fail"}""", InstagramErrorKind.RateLimited)]
    [InlineData(401, """{"message":"login_required","status":"fail"}""", InstagramErrorKind.LoginRequired)]
    [InlineData(200, "<html>app shell</html>", InstagramErrorKind.InvalidResponse)]
    [InlineData(500, """{"message":"oops","status":"fail"}""", InstagramErrorKind.Unknown)]
    public async Task Answers_are_classified_and_keep_the_response(int status, string body, InstagramErrorKind kind)
    {
        var ex = await Assert.ThrowsAsync<InstagramApiException>(() => FollowListReader.ReadAllAsync("1", false,
            (_, _) => Task.FromResult(new PageResult(status, body)), NoDelay, null, default));
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(body, ex.ResponseBody);
        Assert.Contains("/friendships/1/following/", ex.RequestPath);
    }

    [Fact]
    public async Task Pages_after_a_real_pause_are_fetched_off_the_calling_thread()
    {
        // The v2.5.3 crash: after the pause the reader continues on the thread pool, so whatever "fetch" does
        // must not assume the caller's thread. This pins that contract (the WebView2 fetch dispatches itself).
        var threads = new List<int>();
        await FollowListReader.ReadAllAsync("1", false, (path, _) =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            return Task.FromResult(new PageResult(200, path.Contains("max_id")
                ? """{"users":[{"pk":"2"}],"status":"ok"}"""
                : """{"users":[{"pk":"1"}],"next_max_id":"c","status":"ok"}"""));
        }, async (_, ct) => await Task.Delay(10, ct), null, default);
        Assert.Equal(2, threads.Count); // both pages fetched; the second one after a real asynchronous pause
    }

    [Fact]
    public async Task Pauses_between_pages_but_not_before_the_first()
    {
        var pauses = 0;
        await FollowListReader.ReadAllAsync("1", false, (path, _) => Task.FromResult(new PageResult(200, path.Contains("max_id")
                ? """{"users":[{"pk":"2"}],"status":"ok"}"""
                : """{"users":[{"pk":"1"}],"next_max_id":"c","status":"ok"}""")),
            (d, _) => { pauses++; Assert.InRange(d.TotalSeconds, 1.5, 3.5); return Task.CompletedTask; }, null, default);
        Assert.Equal(1, pauses);
    }

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}

public class FollowBackConfirmTests
{
    private static StoryUser U(string pk) => new() { Pk = pk, Username = "user" + pk };
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    // Search answer: the followers whose pk is in "followers" and whose username matches the query.
    private static Func<string, CancellationToken, Task<PageResult>> Search(List<string> paths, params string[] followers) => (path, _) =>
    {
        lock (paths) paths.Add(path);
        var q = Uri.UnescapeDataString(path.Split("query=")[1].Split('&')[0]);
        var hits = followers.Where(pk => q == "user" + pk).Select(pk => $$"""{"pk":"{{pk}}","username":"user{{pk}}"}""");
        return Task.FromResult(new PageResult(200, $$"""{"users":[{{string.Join(",", hits)}}],"status":"ok"}"""));
    };

    [Fact]
    public async Task Candidates_found_by_the_followers_search_are_dropped()
    {
        var paths = new List<string>();
        var check = await FollowBack.ConfirmAsync("me", [U("1"), U("2"), U("3")], Search(paths, "2"), NoDelay, null, default, maxRequests: 10);

        Assert.Equal(["1", "3"], check.NotFollowingBack.Select(u => u.Pk)); // 2 follows back: the list read earlier missed it
        Assert.Equal(1, check.FoundFollowing);
        Assert.Empty(check.Unverified);
        Assert.Equal(3, check.Requests);
        Assert.Contains("/friendships/me/followers/?count=12&query=user1&search_surface=follow_list_page", paths[0]);
    }

    [Fact]
    public async Task A_search_hit_with_another_id_does_not_count()
    {
        // Searching "user1" can return other accounts with similar names; only the same account id counts.
        var check = await FollowBack.ConfirmAsync("me", [U("1")], (path, _) => Task.FromResult(new PageResult(200,
            """{"users":[{"pk":"999","username":"user1_fan"}],"status":"ok"}""")), NoDelay, null, default, 10);
        Assert.Single(check.NotFollowingBack);
    }

    [Fact]
    public async Task When_the_budget_runs_out_the_rest_are_unverified_not_confirmed()
    {
        var check = await FollowBack.ConfirmAsync("me", [U("1"), U("2"), U("3"), U("4")], Search([]), NoDelay, null, default, maxRequests: 2);
        Assert.Equal(["1", "2"], check.NotFollowingBack.Select(u => u.Pk));
        Assert.Equal(["3", "4"], check.Unverified.Select(u => u.Pk));
        Assert.Equal(2, check.Requests);
    }

    [Fact]
    public async Task A_rate_limit_during_the_search_is_reported()
    {
        var ex = await Assert.ThrowsAsync<InstagramStoryViewer.Api.InstagramApiException>(() => FollowBack.ConfirmAsync("me", [U("1")],
            (_, _) => Task.FromResult(new PageResult(429, "")), NoDelay, null, default, 10));
        Assert.Equal(InstagramStoryViewer.Api.InstagramErrorKind.RateLimited, ex.Kind);
    }
}

public class FollowBackControlTests
{
    private static StoryUser U(string pk) => new() { Pk = pk, Username = "user" + pk };

    [Fact]
    public async Task A_failing_control_search_leaves_candidates_unverified_instead_of_confirmed()
    {
        // The search is throttled: it finds nobody, not even a known follower.
        var check = await FollowBack.ConfirmAsync("me", [U("1"), U("2")],
            (_, _) => Task.FromResult(new PageResult(200, """{"users":[],"status":"ok"}""")),
            (_, _) => Task.CompletedTask, null, default, 50, knownFollowers: [U("9")]);
        Assert.Empty(check.NotFollowingBack);
        Assert.Equal(["1", "2"], check.Unverified.Select(u => u.Pk));
    }

    [Fact]
    public async Task A_passing_control_search_confirms()
    {
        var check = await FollowBack.ConfirmAsync("me", [U("1"), U("2")], (path, _) => Task.FromResult(new PageResult(200,
                path.Contains("query=user9") ? """{"users":[{"pk":"9"}],"status":"ok"}""" : """{"users":[],"status":"ok"}""")),
            (_, _) => Task.CompletedTask, null, default, 50, knownFollowers: [U("9")]);
        Assert.Equal(["1", "2"], check.NotFollowingBack.Select(u => u.Pk));
    }

    [Fact]
    public async Task An_empty_page_with_a_next_cursor_does_not_end_the_list()
    {
        var users = await FollowListReader.ReadAllAsync("1", true, (path, _) => Task.FromResult(new PageResult(200,
                path.Contains("max_id=b") ? """{"users":[{"pk":"2"}],"status":"ok"}"""
                : path.Contains("max_id=a") ? """{"users":[],"next_max_id":"b","status":"ok"}"""
                : """{"users":[{"pk":"1"}],"next_max_id":"a","status":"ok"}""")),
            (_, _) => Task.CompletedTask, null, default);
        Assert.Equal(["1", "2"], users.Users.Select(u => u.Pk));
    }
}

public class GraphFollowReaderTests
{
    private static string Page(string ids, bool next, string? cursor, int count = 3)
    {
        var edges = string.Join(",", ids.Split(',').Select(i => "{\"node\":{\"id\":\"" + i + "\",\"username\":\"u" + i + "\",\"is_verified\":false,\"is_private\":true}}"));
        var c = cursor is null ? "null" : "\"" + cursor + "\"";
        return "{\"data\":{\"user\":{\"edge_followed_by\":{\"count\":" + count + ",\"page_info\":{\"has_next_page\":" + (next ? "true" : "false") + ",\"end_cursor\":" + c + "},\"edges\":[" + edges + "]}}},\"status\":\"ok\"}";
    }

    [Fact]
    public async Task Reads_all_pages_with_the_total_count()
    {
        var list = await GraphFollowReader.ReadAllAsync("me", true, (path, _) => Task.FromResult(new PageResult(200,
                path.Contains("%22after%22%3A%22c1%22") ? Page("3", false, null) : Page("1,2", true, "c1"))),
            (_, _) => Task.CompletedTask, null, default, 10);
        Assert.Equal(["1", "2", "3"], list.Users.Select(u => u.Pk));
        Assert.Equal(3, list.Total);
        Assert.True(list.Users[0].IsPrivate);
        Assert.Equal(2, list.Requests);
    }

    [Fact]
    public async Task Rate_limit_and_bad_answers_are_errors()
    {
        var ex = await Assert.ThrowsAsync<InstagramStoryViewer.Api.InstagramApiException>(() => GraphFollowReader.ReadAllAsync("me", true,
            (_, _) => Task.FromResult(new PageResult(429, "")), (_, _) => Task.CompletedTask, null, default, 10));
        Assert.Equal(InstagramStoryViewer.Api.InstagramErrorKind.RateLimited, ex.Kind);
        var bad = await Assert.ThrowsAsync<InstagramStoryViewer.Api.InstagramApiException>(() => GraphFollowReader.ReadAllAsync("me", true,
            (_, _) => Task.FromResult(new PageResult(200, """{"data":{"user":null},"status":"ok"}""")), (_, _) => Task.CompletedTask, null, default, 10));
        Assert.Equal(InstagramStoryViewer.Api.InstagramErrorKind.InvalidResponse, bad.Kind);
    }
}

public class FollowExportReaderTests
{
    [Fact]
    public void Reads_old_and_new_export_formats()
    {
        const string followers = """[{"title":"","string_list_data":[{"href":"https://www.instagram.com/ali","value":"ali","timestamp":1}]},{"title":"","string_list_data":[{"href":"https://www.instagram.com/Sara","value":"Sara","timestamp":1}]}]""";
        const string following = """{"relationships_following":[{"title":"ali","string_list_data":[{"href":"https://www.instagram.com/_u/ali","timestamp":1}]},{"title":"","string_list_data":[{"href":"https://www.instagram.com/_u/omar","timestamp":1}]},{"title":"sara","string_list_data":[]}]}""";
        var lists = FollowExportReader.Read([("followers_1.json", followers), ("following.json", following)]);
        Assert.Equal(["ali", "omar", "sara"], lists.Following);
        Assert.Equal(["omar"], lists.Following.Where(u => !lists.Followers.Contains(u)));
    }

    [Fact]
    public void Missing_file_is_an_error() =>
        Assert.Throws<InvalidDataException>(() => FollowExportReader.Read([("followers_1.json", "[]")]));
}

public class PostFinderOwnerTests
{
    [Fact]
    public void Reads_the_post_owner_id() =>
        Assert.Equal("42", PostFinder.Extract("""{"sections":[{"media":{"code":"abc","taken_at":5,"user":{"pk":"42","username":"dev"}}}]}""")[0].UserPk);
}
