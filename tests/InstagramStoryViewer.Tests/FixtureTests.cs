using InstagramStoryViewer.Api;

namespace InstagramStoryViewer.Tests;

/// <summary>
/// Parses full-size responses shaped like Instagram's (Fixtures/*.json): many fields the app ignores,
/// numbers sent as strings, missing optional parts. When Instagram changes a response in a way that breaks
/// parsing, refreshing the fixture from a real (sanitized) response makes these tests show it first.
/// </summary>
public class FixtureTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static InstagramClient Serve(string name) => TestClient.Create(new FakeHandler(_ => FakeHandler.Json(Fixture(name))));

    [Fact]
    public async Task Reels_tray()
    {
        var tray = await Serve("reels_tray.json").GetStoriesTrayAsync();
        Assert.Equal(2, tray.Count); // the entry without a user is skipped
        var first = tray[0];
        Assert.Equal("first.account", first.User!.Username);
        Assert.Equal("1111111111", first.User.Pk);
        Assert.True(first.HasUnseen);
        Assert.False(tray[1].HasUnseen);
    }

    [Fact]
    public async Task Reels_media_batch_with_photo_and_video()
    {
        var reels = await Serve("reels_media.json").GetStoriesForUsersAsync(["1111111111"]);
        var reel = Assert.Single(reels);
        Assert.Equal(2, reel.Items!.Count);
        var (photo, video) = (reel.Items[0], reel.Items[1]); // sorted oldest first
        Assert.False(photo.IsVideo);
        Assert.True(video.IsVideo);
        Assert.Contains("1080", video.BestUrl);
        Assert.Equal("3333333333333333333", photo.Pk);
        Assert.Equal(true, video.HasLiked);
    }

    [Fact]
    public async Task User_feed()
    {
        var posts = await Serve("feed_user.json").GetUserPostsAsync("1111111111", 4);
        Assert.Equal(2, posts.Count); // the item without a code is skipped
        Assert.Equal("https://www.instagram.com/p/C9NewPost1/", posts[0].Url);
        Assert.Equal(false, posts[0].HasLiked);
        Assert.True(posts[0].TakenAt > posts[1].TakenAt);
    }

    [Fact]
    public async Task Highlights_tray()
    {
        var highlights = await Serve("highlights_tray.json").GetHighlightsAsync("1111111111");
        var h = Assert.Single(highlights);
        Assert.Equal("highlight:17900000000000000", h.Id);
        Assert.Equal("سفر", h.Title);
    }

    [Fact]
    public async Task Web_profile_info()
    {
        var user = await Serve("web_profile_info.json").GetProfileAsync("@first.account");
        Assert.Equal("1111111111", user.Id);
        Assert.True(user.IsPrivate);
    }
}
