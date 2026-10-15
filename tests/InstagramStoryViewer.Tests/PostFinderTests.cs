using InstagramStoryViewer.Social;

namespace InstagramStoryViewer.Tests;

public class PostFinderTests
{
    [Fact]
    public void Finds_posts_at_any_depth_newest_first_once()
    {
        var json = """
        {"data":{"top":{"sections":[{"layout_content":{"medias":[
          {"media":{"pk":"1","code":"AAA","taken_at":100,"media_type":2,"like_count":5,"comment_count":2,
            "user":{"username":"x"},"caption":{"text":"hello #tag"},
            "image_versions2":{"candidates":[{"width":1080,"url":"big"},{"width":320,"url":"small"},{"width":100,"url":"tiny"}]}}},
          {"media":{"pk":2,"code":"BBB","taken_at":"200","carousel_media":[{"image_versions2":{"candidates":[{"width":640,"url":"c"}]}}]}}
        ]}}]},
        "recent":{"sections":[{"medias":[{"media":{"pk":"1","code":"AAA","taken_at":100}}]}]}}}
        """;
        var posts = PostFinder.Extract(json);
        Assert.Equal(["BBB", "AAA"], posts.Select(p => p.Code));
        var a = posts[1];
        Assert.Equal("x", a.Username);
        Assert.Equal("hello #tag", a.Caption);
        Assert.Equal("small", a.ThumbnailUrl);
        Assert.True(a.IsVideo);
        Assert.Equal(5, a.Likes);
        Assert.Equal("c", posts[0].ThumbnailUrl);
        Assert.Equal("https://www.instagram.com/p/BBB/", posts[0].Url);
    }

    [Fact]
    public void Not_json_or_no_posts_gives_nothing()
    {
        Assert.Empty(PostFinder.Extract("<html>"));
        Assert.Empty(PostFinder.Extract("""{"users":[{"pk":"1","username":"a"}]}"""));
    }

    [Theory]
    [InlineData("#Travel", "Travel")]
    [InlineData(" food porn ", "foodporn")]
    public void Normalizes_tags(string input, string tag) => Assert.Equal(tag, PostFinder.NormalizeTag(input));

    [Fact]
    public void Follower_changes()
    {
        var (lost, gained) = FollowerChanges.Compare([new("1", "a"), new("2", "b")], [new("2", "b"), new("3", "c")]);
        Assert.Equal(["1"], lost.Select(f => f.Pk));
        Assert.Equal(["3"], gained.Select(f => f.Pk));
    }
}
