using InstagramStoryViewer.Api;

namespace InstagramStoryViewer.Tests;

public class SessionCookiesTests
{
    [Fact]
    public void Parses_full_cookie_header()
    {
        var c = SessionCookies.Parse("Cookie: csrftoken=abc; sessionid=4242%3AXyZ%3A12; mid=q");
        Assert.Equal("4242%3AXyZ%3A12", c.SessionId);
        Assert.Equal("abc", c.CsrfToken);
        Assert.Equal("4242", c.DsUserId); // derived from the sessionid prefix
    }

    [Fact]
    public void Parses_bare_sessionid()
    {
        var c = SessionCookies.Parse("4242%3AXyZ");
        Assert.Equal("4242%3AXyZ", c.SessionId);
        Assert.Contains("sessionid=4242%3AXyZ", c.ToHeader());
    }

    [Fact]
    public void Rejects_input_without_sessionid() =>
        Assert.Throws<ArgumentException>(() => SessionCookies.Parse("csrftoken=abc"));

    [Fact]
    public void Applies_rotated_cookies_and_raises_changed()
    {
        var c = SessionCookies.Parse(TestClient.Cookie);
        var raised = 0;
        c.Changed += (_, _) => raised++;

        c.Apply(["sessionid=12345%3Anew%3A2; Path=/; Secure; HttpOnly", "rur=\"CLN\\05412345\"; Path=/", "csrftoken=tok2; Path=/"]);

        Assert.Equal(1, raised);
        Assert.Equal("12345%3Anew%3A2", c.SessionId);
        Assert.Equal("tok2", c.CsrfToken);
        Assert.Contains("rur=", c.ToHeader());
        Assert.False(c.IsRevoked);
    }

    [Fact]
    public void Same_values_do_not_raise_changed()
    {
        var c = SessionCookies.Parse(TestClient.Cookie);
        var raised = 0;
        c.Changed += (_, _) => raised++;
        c.Apply(["csrftoken=tok; Path=/"]);
        Assert.Equal(0, raised);
    }

    [Theory]
    [InlineData("sessionid=\"\"; Path=/")]
    [InlineData("sessionid=x; Max-Age=0; Path=/")]
    [InlineData("sessionid=x; expires=Thu, 01-Jan-1970 00:00:00 GMT; Path=/")]
    public void Deleting_sessionid_marks_session_revoked(string header)
    {
        var c = SessionCookies.Parse(TestClient.Cookie);
        c.Apply([header]);
        Assert.True(c.IsRevoked);
    }

    [Fact]
    public void Deleting_other_cookie_removes_it()
    {
        var c = SessionCookies.Parse(TestClient.Cookie + "; rur=abc");
        c.Apply(["rur=; Max-Age=0"]);
        Assert.DoesNotContain("rur=", c.ToHeader());
        Assert.False(c.IsRevoked);
    }
}
