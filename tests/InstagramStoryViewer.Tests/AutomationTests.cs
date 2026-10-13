using InstagramStoryViewer.Automation;
using InstagramStoryViewer.Social;

namespace InstagramStoryViewer.Tests;

public class AutomationTests
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 13, 23, 59, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Daily_count_rolls_over_at_local_midnight()
    {
        var c = new DailyCount();
        c.Add(5, Day1);
        Assert.Equal(5, c.Today(Day1));
        Assert.Equal(15, c.Remaining(20, Day1));

        var nextDay = Day1.AddMinutes(2);
        Assert.Equal(0, c.Today(nextDay));      // reading does not change the stored value...
        Assert.Equal(5, c.Count);
        c.Add(1, nextDay);                       // ...adding on the new day starts from zero
        Assert.Equal(1, c.Today(nextDay));
    }

    [Fact]
    public void Remaining_is_never_negative()
    {
        var c = new DailyCount();
        c.Add(30, Day1);
        Assert.Equal(0, c.Remaining(10, Day1));
    }

    [Theory]
    [InlineData(1, 8, 0, false)]
    [InlineData(1, 8, 1, true)]
    [InlineData(1, 8, 7, true)]
    [InlineData(1, 8, 8, false)]
    [InlineData(23, 7, 23, true)]   // wraps past midnight
    [InlineData(23, 7, 3, true)]
    [InlineData(23, 7, 12, false)]
    [InlineData(5, 5, 5, false)]    // empty range
    public void Quiet_hours(int from, int to, int hour, bool quiet) =>
        Assert.Equal(quiet, QuietHours.Contains(true, from, to, hour));

    [Fact]
    public void Quiet_hours_off_is_never_quiet() => Assert.False(QuietHours.Contains(false, 0, 23, 5));

    [Fact]
    public async Task Job_queue_runs_one_at_a_time_in_order_and_skips_duplicates()
    {
        var log = new List<string>();
        using var q = new JobQueue((_, ex) => log.Add("error " + ex.Message));
        var gate = new TaskCompletionSource();

        Assert.True(q.Enqueue("watch", async _ => { log.Add("watch start"); await gate.Task; log.Add("watch end"); }));
        Assert.True(q.Enqueue("like", _ => { log.Add("like"); return Task.CompletedTask; }));
        Assert.False(q.Enqueue("like", _ => { log.Add("like again"); return Task.CompletedTask; }));

        await Task.Delay(50);
        Assert.Equal("watch", q.Running);
        Assert.False(q.Enqueue("watch", _ => Task.CompletedTask)); // still running → not queued twice
        gate.SetResult();
        await q.WhenIdleAsync();

        Assert.Equal(["watch start", "watch end", "like"], log);
    }

    [Fact]
    public async Task Job_queue_cancel_all_stops_the_running_job_and_drops_the_rest()
    {
        var ran = new List<string>();
        using var q = new JobQueue((_, _) => ran.Add("error"));
        q.Enqueue("long", async ct => { ran.Add("long"); await Task.Delay(Timeout.Infinite, ct); });
        q.Enqueue("next", _ => { ran.Add("next"); return Task.CompletedTask; });
        await Task.Delay(50);

        q.CancelAll();
        await q.WhenIdleAsync();
        Assert.Equal(["long"], ran);   // cancelled quietly, "next" dropped

        q.Enqueue("after", _ => { ran.Add("after"); return Task.CompletedTask; });
        await q.WhenIdleAsync();
        Assert.Equal(["long", "after"], ran);
    }

    [Fact]
    public async Task Job_queue_reports_errors_and_keeps_going()
    {
        var errors = new List<string>();
        var ran = false;
        using var q = new JobQueue((name, ex) => errors.Add($"{name}: {ex.Message}"));
        q.Enqueue("bad", _ => throw new InvalidOperationException("boom"));
        q.Enqueue("good", _ => { ran = true; return Task.CompletedTask; });
        await q.WhenIdleAsync();
        Assert.Equal(["bad: boom"], errors);
        Assert.True(ran);
    }

    [Theory]
    [InlineData("https://www.instagram.com/p/C8abc-12_x/", "p", "C8abc-12_x")]
    [InlineData("instagram.com/p/C8abc12x", "p", "C8abc12x")]
    [InlineData("https://instagram.com/reel/DAbCdEf123/?igsh=xyz", "reel", "DAbCdEf123")]
    [InlineData("https://www.instagram.com/reels/DAbCdEf123/", "reel", "DAbCdEf123")]
    [InlineData("https://www.instagram.com/someone.name/p/C8abc12x/", "p", "C8abc12x")]
    [InlineData("  https://m.instagram.com/tv/B1234567/  ", "tv", "B1234567")]
    public void Parses_post_links(string text, string kind, string code)
    {
        var link = PostLink.TryParse(text);
        Assert.NotNull(link);
        Assert.Equal(kind, link.Kind);
        Assert.Equal(code, link.Shortcode);
        Assert.Equal($"https://www.instagram.com/{kind}/{code}/", link.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://www.instagram.com/someone/")]
    [InlineData("https://www.instagram.com/stories/someone/123/")]
    [InlineData("https://evil.example/instagram.com/p/C8abc12x/")]
    [InlineData("https://www.instagram.com.evil.example/p/C8abc12x/")]
    public void Rejects_anything_that_is_not_a_single_post(string text) => Assert.Null(PostLink.TryParse(text));

    [Fact]
    public void Comment_rules()
    {
        Assert.Null(CommentRules.Validate("ما شاء الله 👏 @friend #تصوير"));
        Assert.NotNull(CommentRules.Validate("   "));
        Assert.NotNull(CommentRules.Validate(new string('a', CommentRules.MaxLength + 1)));
        Assert.NotNull(CommentRules.Validate(string.Join(' ', Enumerable.Range(0, 31).Select(i => "#t" + i))));
        Assert.NotNull(CommentRules.Validate(string.Join(' ', Enumerable.Range(0, 6).Select(i => "@u" + i))));
        Assert.Null(CommentRules.Validate("mail me at a@b.com")); // an e-mail address is not a mention
    }
}
