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

    [Theory]
    [InlineData(0, 0, 2)]
    [InlineData(12, 12, 2)]
    [InlineData(13, 5, 3)]
    [InlineData(400, 389, 67)]
    public void Estimates_requests(int following, int followers, int expected) =>
        Assert.Equal(expected, FollowBack.EstimateRequests(following, followers));

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
        var users = await FollowListReader.ReadAllAsync("123", followers: false, Fetch, NoDelay, new SyncProgress(progress.Add), default);

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
        Assert.Single(users);
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
    public void User_info_carries_the_counts()
    {
        var me = FollowListReader.ParseUserInfo(
            new PageResult(200, """{"user":{"pk":"12345","username":"me","follower_count":321,"following_count":456},"status":"ok"}"""), "/x");
        Assert.Equal(321, me.FollowerCount);
        Assert.Equal(456, me.FollowingCount);
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
