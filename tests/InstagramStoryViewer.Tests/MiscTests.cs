using System.Text.Json;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Tests;

public class DiagnosticsSanitizerTests
{
    [Fact]
    public void Masks_tokens_in_json_and_cookie_pairs()
    {
        var input = """{"csrf_token":"SECRET1","viewerId":"123456","fb_dtsg":"SECRET2"} Cookie: sessionid=SECRET3; ds_user_id=99""";
        var output = DiagnosticsSanitizer.Sanitize(input);

        Assert.DoesNotContain("SECRET1", output);
        Assert.DoesNotContain("SECRET2", output);
        Assert.DoesNotContain("SECRET3", output);
        Assert.DoesNotContain("123456", output);
        Assert.Contains("\"csrf_token\":\"***\"", output);
        Assert.Contains("sessionid=***", output);
    }

    [Fact]
    public void Keeps_harmless_text() =>
        Assert.Equal("<title>Page Not Found</title>", DiagnosticsSanitizer.Sanitize("<title>Page Not Found</title>"));

    [Fact]
    public void Truncates_huge_bodies() =>
        Assert.True(DiagnosticsSanitizer.Sanitize(new string('a', 500_000)).Length < 70_000);
}

public class ModelTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Picks_best_video_and_small_thumbnail()
    {
        var item = JsonSerializer.Deserialize<StoryItem>("""
            {"pk":999,"taken_at":1,"media_type":2,
             "video_versions":[{"width":480,"height":854,"url":"lo"},{"width":720,"height":1280,"url":"hi"}],
             "image_versions2":{"candidates":[{"width":1080,"height":1920,"url":"big"},{"width":320,"height":568,"url":"small"},{"width":100,"height":178,"url":"tiny"}]}}
            """, Options)!;

        Assert.Equal("999", item.Pk); // numeric id read as string
        Assert.True(item.IsVideo);
        Assert.Equal("hi", item.BestUrl);
        Assert.Equal("big", item.CoverUrl);
        Assert.Equal("small", item.ThumbnailUrl); // smallest that is still >= 150px wide
        Assert.Equal("720×1280", item.Resolution);
    }

    [Fact]
    public void Expiry_defaults_to_24_hours() =>
        Assert.Equal(TimeSpan.FromHours(24), new StoryItem { TakenAt = 1000 }.ExpiresAtTime - new StoryItem { TakenAt = 1000 }.TakenAtTime);
}
