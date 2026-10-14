using System.Net;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Models;
using InstagramStoryViewer.Social;

namespace InstagramStoryViewer.Tests;

public class FollowBackTests
{
    private static StoryUser U(string pk) => new() { Pk = pk, Username = "u" + pk };

    [Fact]
    public void Not_following_back_is_following_minus_followers()
    {
        var result = FollowBack.NotFollowingBack([U("1"), U("2"), U("3"), U("4")], [U("2"), U("4"), U("9")]);
        Assert.Equal(["1", "3"], result.Select(u => u.Pk));
    }

    [Theory]
    [InlineData(0, 0, 2)]
    [InlineData(50, 50, 2)]
    [InlineData(51, 10, 3)]
    [InlineData(400, 389, 16)]
    public void Estimates_requests(int following, int followers, int expected) =>
        Assert.Equal(expected, FollowBack.EstimateRequests(following, followers));

    [Fact]
    public async Task Reads_every_page_of_a_follow_list()
    {
        var handler = new FakeHandler(req =>
        {
            var q = req.RequestUri!.Query;
            return q.Contains("max_id=p2")
                ? FakeHandler.Json("""{"users":[{"pk":3,"username":"c"},{"pk":"2","username":"b"}],"next_max_id":null,"status":"ok"}""")
                : FakeHandler.Json("""{"users":[{"pk":"1","username":"a","is_verified":true},{"pk":"2","username":"b"}],"next_max_id":"p2","status":"ok"}""");
        });
        var progress = new List<int>();
        var users = await TestClient.Create(handler).GetFollowListAsync("123", InstagramClient.FollowList.Following, new SyncProgress(progress.Add));

        Assert.Equal(["1", "2", "3"], users.Select(u => u.Pk)); // duplicate across pages kept once
        Assert.True(users[0].IsVerified);
        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith("/api/v1/friendships/123/following/", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains($"count={InstagramClient.FollowListPageSize}", handler.Requests[0].RequestUri!.Query);
        Assert.Equal([2, 3], progress);
    }

    [Fact]
    public async Task Followers_use_the_follow_list_surface_and_stop_on_a_repeated_cursor()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"users":[{"pk":"1"}],"next_max_id":"same","status":"ok"}"""));
        await TestClient.Create(handler).GetFollowListAsync("123", InstagramClient.FollowList.Followers);
        Assert.Equal(2, handler.Requests.Count); // first page, then the cursor repeats → stop
        Assert.Contains("search_surface=follow_list_page", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task Hourly_cap_stops_a_long_list()
    {
        var page = 0;
        var handler = new FakeHandler(_ => FakeHandler.Json($$"""{"users":[{"pk":"{{++page}}"}],"next_max_id":"c{{page}}","status":"ok"}"""));
        var client = TestClient.Create(handler);
        client.MaxRequestsPerHour = 3;
        var ex = await Assert.ThrowsAsync<InstagramApiException>(() => client.GetFollowListAsync("1", InstagramClient.FollowList.Following));
        Assert.Equal(InstagramErrorKind.RateLimited, ex.Kind);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task User_info_carries_the_counts()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"user":{"pk":"12345","username":"me","follower_count":321,"following_count":456},"status":"ok"}"""));
        var me = await TestClient.Create(handler).GetCurrentUserAsync();
        Assert.Equal(321, me.FollowerCount);
        Assert.Equal(456, me.FollowingCount);
    }

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
