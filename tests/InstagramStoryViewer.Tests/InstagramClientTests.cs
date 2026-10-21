using System.Net;
using InstagramStoryViewer.Api;

namespace InstagramStoryViewer.Tests;

public class InstagramClientTests
{
    private const string TrayJson = """{"tray":[{"id":5,"user":{"pk":5,"username":"x"},"latest_reel_media":200,"seen":100}],"status":"ok"}""";

    [Fact]
    public async Task Parses_tray()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(TrayJson));
        var tray = await TestClient.Create(handler).GetStoriesTrayAsync();
        var reel = Assert.Single(tray);
        Assert.Equal("x", reel.User!.Username);
        Assert.True(reel.HasUnseen);
    }

    [Fact]
    public async Task Falls_back_to_next_host_when_one_returns_html_and_remembers_it()
    {
        var handler = new FakeHandler(req => req.RequestUri!.Host == "www.instagram.com"
            ? FakeHandler.Html("<html><title>Instagram</title></html>")
            : FakeHandler.Json(TrayJson));
        var client = TestClient.Create(handler);

        await client.GetStoriesTrayAsync();
        Assert.Equal(["www.instagram.com", "i.instagram.com"], handler.Requests.Select(r => r.RequestUri!.Host));
        Assert.Equal("web-i", client.ActiveProfile);

        handler.Requests.Clear();
        await client.GetStoriesTrayAsync();
        Assert.Equal("i.instagram.com", Assert.Single(handler.Requests).RequestUri!.Host);
    }

    [Fact]
    public async Task Html_from_every_host_is_reported_with_the_response_body()
    {
        var handler = new FakeHandler(_ => FakeHandler.Html("<html><title>Page Not Found</title></html>", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<InstagramApiException>(() => TestClient.Create(handler).GetStoriesTrayAsync());
        Assert.Equal(InstagramErrorKind.InvalidResponse, ex.Kind);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("Page Not Found", ex.ResponseBody);
    }

    [Fact]
    public async Task Login_required_json_is_classified()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"message":"login_required","status":"fail"}""", HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<InstagramApiException>(() => TestClient.Create(handler).GetStoriesTrayAsync());
        Assert.Equal(InstagramErrorKind.LoginRequired, ex.Kind);
    }

    [Fact]
    public async Task Rate_limit_message_is_classified()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"message":"Please wait a few minutes before you try again.","status":"fail"}""",
            HttpStatusCode.BadRequest));
        var ex = await Assert.ThrowsAsync<InstagramApiException>(() => TestClient.Create(handler).GetStoriesTrayAsync());
        Assert.Equal(InstagramErrorKind.RateLimited, ex.Kind);
    }

    [Fact]
    public async Task Strips_anti_hijacking_prefix()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("for (;;);" + TrayJson));
        Assert.Single(await TestClient.Create(handler).GetStoriesTrayAsync());
    }

    [Fact]
    public async Task Server_deleting_sessionid_stops_further_requests()
    {
        var handler = new FakeHandler(_ =>
        {
            var res = FakeHandler.Json(TrayJson);
            res.Headers.Add("Set-Cookie", "sessionid=\"\"; Max-Age=0; Path=/");
            return res;
        });
        var client = TestClient.Create(handler);

        var ex = await Assert.ThrowsAsync<InstagramApiException>(() => client.GetStoriesTrayAsync());
        Assert.Equal(InstagramErrorKind.LoginRequired, ex.Kind);
        Assert.True(client.Cookies.IsRevoked);

        handler.Requests.Clear();
        await Assert.ThrowsAsync<InstagramApiException>(() => client.GetStoriesTrayAsync());
        Assert.Empty(handler.Requests); // nothing is sent with a revoked session
    }

    [Fact]
    public async Task Rotated_cookies_are_sent_on_the_next_request()
    {
        var call = 0;
        var handler = new FakeHandler(_ =>
        {
            var res = FakeHandler.Json(TrayJson);
            if (call++ == 0) res.Headers.Add("Set-Cookie", "csrftoken=rotated; Path=/");
            return res;
        });
        var client = TestClient.Create(handler);
        await client.GetStoriesTrayAsync();
        await client.GetStoriesTrayAsync();
        Assert.Equal("rotated", handler.Requests[1].Headers.GetValues("X-CSRFToken").Single());
    }

    [Fact]
    public async Task Hourly_cap_refuses_extra_requests()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(TrayJson));
        var client = TestClient.Create(handler);
        client.MaxRequestsPerHour = 2;

        await client.GetStoriesTrayAsync();
        await client.GetStoriesTrayAsync();
        var ex = await Assert.ThrowsAsync<InstagramApiException>(() => client.GetStoriesTrayAsync());
        Assert.Equal(InstagramErrorKind.RateLimited, ex.Kind);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, client.RequestsLastHour);
    }

    [Fact]
    public async Task Batch_stories_request_uses_all_ids()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""
            {"reels_media":[{"id":"1","user":{"pk":"1","username":"a"},"items":[{"pk":"9","taken_at":2,"media_type":1},{"pk":"8","taken_at":1,"media_type":1}]},
                            {"id":"2","user":{"pk":"2","username":"b"},"items":[]}],"status":"ok"}
            """));
        var reels = await TestClient.Create(handler).GetStoriesForUsersAsync(["1", "2", "1"]);

        Assert.Contains("reel_ids=1&reel_ids=2", handler.Requests.Single().RequestUri!.Query);
        var reel = Assert.Single(reels); // the empty reel is dropped
        Assert.Equal(["8", "9"], reel.Items!.Select(i => i.Pk)); // sorted oldest first
    }
}

public class HighlightTests
{
    [Fact]
    public async Task Parses_highlights_and_loads_one_as_a_reel()
    {
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath.Contains("highlights_tray")
            ? FakeHandler.Json("""
                {"tray":[{"id":"highlight:17890","title":"Travel","media_count":3,
                          "cover_media":{"cropped_image_version":{"width":150,"height":150,"url":"cover"}}}],"status":"ok"}
                """)
            : FakeHandler.Json("""
                {"reels":{"highlight:17890":{"id":"highlight:17890","items":[{"pk":"1","taken_at":5,"media_type":1}]}},"status":"ok"}
                """));
        var client = TestClient.Create(handler);

        var h = Assert.Single(await client.GetHighlightsAsync("42"));
        Assert.Equal("Travel", h.Title);
        Assert.Equal("cover", h.CoverMedia!.CroppedImageVersion!.Url);

        var reel = await client.GetUserStoriesAsync(h.Id!);
        Assert.Single(reel!.Items!);
        Assert.Contains("/api/v1/highlights/42/highlights_tray/", handler.Requests[0].RequestUri!.AbsolutePath);
    }
}

public class UserPostsTests
{
    [Fact]
    public async Task Parses_user_posts_newest_first_with_like_state()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""
            {"items":[
              {"pk":11,"code":"AAA","taken_at":100,"media_type":1,"has_liked":true,"user":{"pk":5,"username":"x"}},
              {"pk":12,"code":"BBB","taken_at":300,"media_type":2,"has_liked":false,"user":{"pk":5,"username":"x"}},
              {"pk":13,"taken_at":200,"media_type":1}
            ],"status":"ok"}
            """));
        var posts = await TestClient.Create(handler).GetUserPostsAsync("5", count: 3);

        Assert.Equal(["BBB", "AAA"], posts.Select(p => p.Code)); // newest first, item without code dropped
        Assert.False(posts[0].HasLiked);
        Assert.Equal("https://www.instagram.com/p/BBB/", posts[0].Url);
        Assert.Contains("/api/v1/feed/user/5/", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("count=3", handler.Requests[0].RequestUri!.Query);
    }

    private static HttpResponseMessage Fail500() =>
        FakeHandler.Json("""{"message":"We're sorry, but something went wrong. Please try again.","status":"fail"}""",
            System.Net.HttpStatusCode.InternalServerError);

    private static HttpResponseMessage Reels(IEnumerable<string> ids) => FakeHandler.Json(
        "{\"reels_media\":[" + string.Join(",", ids.Select(id =>
            "{\"id\":\"" + id + "\",\"user\":{\"pk\":\"" + id + "\",\"username\":\"u" + id + "\"},\"items\":[{\"pk\":\"m" + id + "\",\"taken_at\":1,\"media_type\":1}]}")) + "],\"status\":\"ok\"}");

    private static List<string> Ids(HttpRequestMessage r) =>
        System.Text.RegularExpressions.Regex.Matches(r.RequestUri!.Query, "reel_ids=([^&]+)").Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public async Task One_account_that_breaks_the_batch_is_skipped_without_retrying_the_same_batch()
    {
        var handler = new FakeHandler(r => Ids(r).Contains("3") ? Fail500() : Reels(Ids(r)));
        var reels = await TestClient.Create(handler).GetStoriesForUsersAsync(["1", "2", "3", "4"]);

        Assert.Equal(["1", "2", "4"], reels.Select(r => r.User!.Pk).Order());
        // [1,2,3,4] fails, [1,2] ok, [3,4] fails, [3] fails and is skipped, [4] ok: no request is sent twice.
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(handler.Requests.Count, handler.Requests.Select(r => r.RequestUri!.Query).Distinct().Count());
    }

    [Fact]
    public async Task An_outage_still_fails_after_a_few_requests()
    {
        var handler = new FakeHandler(_ => Fail500());
        var ex = await Assert.ThrowsAsync<InstagramStoryViewer.Api.InstagramApiException>(() =>
            TestClient.Create(handler).GetStoriesForUsersAsync(Enumerable.Range(1, 20).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.True(handler.Requests.Count <= 12, $"{handler.Requests.Count} requests");
    }

    [Fact]
    public async Task Other_calls_still_retry_a_500()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls < 3 ? Fail500()
            : FakeHandler.Json("""{"tray":[],"status":"ok"}"""));
        await TestClient.Create(handler).GetHighlightsAsync("1");
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task One_account_answered_with_500_falls_back_to_its_story_endpoint()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/feed/user/7/story/", StringComparison.Ordinal)
            ? FakeHandler.Json("""{"reel":{"id":"7","user":{"pk":"7","username":"u7"},"items":[{"pk":"b","taken_at":2,"media_type":1},{"pk":"a","taken_at":1,"media_type":1}]},"status":"ok"}""")
            : Fail500());
        var reel = await TestClient.Create(handler).GetUserStoriesAsync("7");

        Assert.Equal(["a", "b"], reel!.Items!.Select(i => i.Pk));
        Assert.Equal(2, handler.Requests.Count); // reels_media once (no retry), then the fallback once
    }

    [Fact]
    public async Task When_the_fallback_fails_too_the_original_500_is_reported()
    {
        var handler = new FakeHandler(_ => Fail500());
        var ex = await Assert.ThrowsAsync<InstagramStoryViewer.Api.InstagramApiException>(() => TestClient.Create(handler).GetUserStoriesAsync("7"));
        Assert.Contains("reels_media", ex.RequestPath);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Several_refused_accounts_do_not_stop_the_others_and_are_left_out_next_time()
    {
        var bad = new HashSet<string> { "3", "11" };
        var handler = new FakeHandler(r => Ids(r).Any(bad.Contains) ? Fail500() : Reels(Ids(r)));
        var client = TestClient.Create(handler);
        var all = Enumerable.Range(1, 20).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();

        var reels = await client.GetStoriesForUsersAsync(all);
        Assert.Equal(18, reels.Count);

        handler.Requests.Clear();
        reels = await client.GetStoriesForUsersAsync(all);
        Assert.Equal(18, reels.Count);
        Assert.Single(handler.Requests); // the refused accounts are no longer asked for
        Assert.DoesNotContain(Ids(handler.Requests[0]), bad.Contains);
    }
}
