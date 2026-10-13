using System.Net;
using InstagramStoryViewer.Api;

namespace InstagramStoryViewer.Tests;

public class ResilienceTests
{
    private const string TrayJson = """{"tray":[],"status":"ok"}""";

    [Fact]
    public async Task Server_error_is_retried_and_then_succeeds()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls < 3
            ? FakeHandler.Json("{}", HttpStatusCode.ServiceUnavailable)
            : FakeHandler.Json(TrayJson));
        await TestClient.Create(handler).GetStoriesTrayAsync();
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Gives_up_after_three_attempts()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"message":"oops","status":"fail"}""", HttpStatusCode.InternalServerError));
        var ex = await Assert.ThrowsAsync<InstagramApiException>(() => TestClient.Create(handler).GetStoriesTrayAsync());
        Assert.Equal(InstagramErrorKind.Unknown, ex.Kind);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Network_error_becomes_a_network_error_after_retries()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("no route"));
        var ex = await Assert.ThrowsAsync<InstagramApiException>(() => TestClient.Create(handler).GetStoriesTrayAsync());
        Assert.Equal(InstagramErrorKind.Network, ex.Kind);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Instagram_rate_limit_pauses_all_requests_until_the_cooldown_ends()
    {
        var limited = true;
        var handler = new FakeHandler(_ => limited
            ? FakeHandler.Json("""{"message":"Please wait a few minutes before you try again.","status":"fail"}""", HttpStatusCode.BadRequest)
            : FakeHandler.Json(TrayJson));
        var clock = new InstantTimeProvider();
        var client = TestClient.Create(handler, time: clock);

        await Assert.ThrowsAsync<InstagramApiException>(() => client.GetStoriesTrayAsync());
        Assert.NotNull(client.CoolingDownUntil);
        var sent = handler.Requests.Count;

        // Paused: refused locally, nothing reaches Instagram.
        limited = false;
        var ex = await Assert.ThrowsAsync<InstagramApiException>(() => client.GetStoriesTrayAsync());
        Assert.Equal(InstagramErrorKind.RateLimited, ex.Kind);
        Assert.Equal(sent, handler.Requests.Count);

        // After the cool-down, requests flow again.
        clock.Now += InstagramClient.RateLimitCooldown + TimeSpan.FromSeconds(1);
        Assert.Null(client.CoolingDownUntil);
        await client.GetStoriesTrayAsync();
    }

    [Fact]
    public async Task Own_hourly_cap_does_not_start_a_cooldown()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(TrayJson));
        var client = TestClient.Create(handler);
        client.MaxRequestsPerHour = 1;
        await client.GetStoriesTrayAsync();
        await Assert.ThrowsAsync<InstagramApiException>(() => client.GetStoriesTrayAsync());
        Assert.Null(client.CoolingDownUntil);
    }

    [Fact]
    public async Task Cancelling_stops_without_retrying()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestClient.Create(handler).GetStoriesTrayAsync(cts.Token));
        Assert.Single(handler.Requests);
    }
}
