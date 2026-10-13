using InstagramStoryViewer.Api;

namespace InstagramStoryViewer.Tests;

internal static class TestClient
{
    public const string Cookie = "sessionid=12345%3Aabc%3A1; csrftoken=tok; ds_user_id=12345";

    /// <summary>A client whose throttling and retry delays complete immediately.</summary>
    public static InstagramClient Create(FakeHandler handler, string cookie = Cookie, TimeProvider? time = null) =>
        new(SessionCookies.Parse(cookie), null, handler, (_, _) => Task.CompletedTask, time ?? new InstantTimeProvider());
}

/// <summary>
/// A clock whose timers fire at once, so Polly's retry delays do not slow the tests down. The current time
/// can be moved forward to test time-based rules (cool-downs).
/// </summary>
internal sealed class InstantTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan) ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => default;
    }
}
