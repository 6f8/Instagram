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
