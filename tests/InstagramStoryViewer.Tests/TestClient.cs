using InstagramStoryViewer.Api;

namespace InstagramStoryViewer.Tests;

internal static class TestClient
{
    public const string Cookie = "sessionid=12345%3Aabc%3A1; csrftoken=tok; ds_user_id=12345";

    /// <summary>A client whose throttling/backoff delays complete immediately.</summary>
    public static InstagramClient Create(FakeHandler handler, string cookie = Cookie) =>
        new(SessionCookies.Parse(cookie), null, handler, (_, _) => Task.CompletedTask);
}
