using InstagramStoryViewer.Api;

namespace InstagramStoryViewer.Tests;

public class StoryPageReaderTests
{
    private const string Item1 = """{"pk":"11","id":"11_7","taken_at":200,"media_type":1,"image_versions2":{"candidates":[{"url":"https://cdn/a.jpg","width":1080,"height":1920}]}}""";
    private const string Item2 = """{"pk":"10","id":"10_7","taken_at":100,"media_type":2,"video_versions":[{"url":"https://cdn/b.mp4","width":720,"height":1280}],"image_versions2":{"candidates":[]}}""";

    [Fact]
    public void Finds_the_reel_in_a_graphql_answer_nested_anywhere()
    {
        var json = $$$"""{"data":{"xdt_api__v1__feed__reels_media":{"reels_media":[{"id":"7","user":{"pk":"7","username":"TamerHosny"},"items":[{{{Item1}}},{{{Item2}}}]}]}},"extensions":{}}""";
        var reel = StoryPageReader.FindReel([json], "tamerhosny", null);

        Assert.NotNull(reel);
        Assert.Equal(["10", "11"], reel.Items!.Select(i => i.Pk)); // oldest first
        Assert.Equal("7", reel.User!.Pk);
    }

    [Fact]
    public void Reads_several_documents_on_separate_lines_and_ignores_other_accounts()
    {
        var other = $$$"""{"reels_media":[{"id":"8","user":{"pk":"8","username":"someone"},"items":[{{{Item1}}},{{{Item1}}},{{{Item2}}}]}]}""";
        var mine = $$$"""{"reels_media":[{"id":"7","user":{"pk":"7","username":"x"},"items":[{{{Item2}}}]}]}""";
        var reel = StoryPageReader.FindReel(["for (;;);" + other + "\n" + mine, "not json"], "tamerhosny", "7");

        Assert.Equal("10", Assert.Single(reel!.Items!).Pk);
    }

    [Fact]
    public void Nothing_found_is_null() =>
        Assert.Null(StoryPageReader.FindReel(["""{"data":{"items":[{"pk":"1"}]}}""", ""], "tamerhosny", "7"));
}
