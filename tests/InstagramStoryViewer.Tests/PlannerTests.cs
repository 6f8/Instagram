using InstagramStoryViewer.Automation;
using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Tests;

public class PlannerTests
{
    private static readonly AccountTarget A = new("1", "alice");
    private static readonly AccountTarget B = new("2", "bob");

    private static Reel ReelOf(string userId, long seen, params (string pk, long takenAt, bool liked)[] items) => new()
    {
        Id = userId,
        User = new StoryUser { Pk = userId },
        Seen = seen,
        Items = items.Select(i => new StoryItem { Pk = i.pk, TakenAt = i.takenAt, HasLiked = i.liked }).ToList()
    };

    [Fact]
    public void Only_stories_after_the_seen_marker_oldest_first()
    {
        var reels = new[] { ReelOf("1", seen: 100, ("a3", 300, false), ("a1", 100, false), ("a2", 200, false)) };
        var plan = Planner.UnseenStories(reels, [A], budget: 10);
        Assert.Equal(["a2", "a3"], plan.Select(p => p.MediaPk));
        Assert.Equal("https://www.instagram.com/stories/alice/a2/", plan[0].Url);
    }

    [Fact]
    public void Budget_caps_the_round_and_unknown_accounts_are_ignored()
    {
        var reels = new[]
        {
            ReelOf("1", 0, ("a1", 1, false), ("a2", 2, false)),
            ReelOf("9", 0, ("x1", 1, false)),   // not in the candidate list
            ReelOf("2", 0, ("b1", 1, false))
        };
        Assert.Equal(["a1", "a2", "b1"], Planner.UnseenStories(reels, [A, B], 10).Select(p => p.MediaPk));
        Assert.Equal(["a1", "a2"], Planner.UnseenStories(reels, [A, B], 2).Select(p => p.MediaPk));
        Assert.Empty(Planner.UnseenStories(reels, [A, B], 0));
    }

    [Fact]
    public void Likes_skip_liked_and_already_handled_stories()
    {
        var reels = new[] { ReelOf("1", 0, ("a1", 1, true), ("a2", 2, false), ("a3", 3, false), ("a4", 4, false)) };
        var plan = Planner.StoriesToLike(reels, [A], pk => pk == "a3", budget: 5);
        Assert.Equal(["a2", "a4"], plan.Select(p => p.MediaPk));
    }

    [Fact]
    public void Posts_to_like_are_recent_unliked_and_new()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var posts = new[]
        {
            new FeedPost { Pk = "p1", Code = "C1", TakenAt = 1_000_000 - 3600, HasLiked = false },
            new FeedPost { Pk = "p2", Code = "C2", TakenAt = 1_000_000 - 3600, HasLiked = true },
            new FeedPost { Pk = "p3", Code = "C3", TakenAt = 1_000_000 - 10 * 86400, HasLiked = false },  // too old
            new FeedPost { Pk = "p4", Code = "C4", TakenAt = 1_000_000, HasLiked = false },               // handled
            new FeedPost { Pk = "p5", Code = null, TakenAt = 1_000_000 }                                   // no link
        };
        var plan = Planner.PostsToLike(A, posts, pk => pk == "p4", TimeSpan.FromDays(3), now);
        var p = Assert.Single(plan);
        Assert.Equal("https://www.instagram.com/p/C1/", p.Url);
        Assert.True(p.IsPost);
    }

    [Fact]
    public void Round_robin_wraps_and_advances()
    {
        var all = new[] { "a", "b", "c", "d", "e" };
        var (first, c1) = Planner.RoundRobin(all, 0, 3);
        var (second, c2) = Planner.RoundRobin(all, c1, 3);
        Assert.Equal(["a", "b", "c"], first);
        Assert.Equal(["d", "e", "a"], second);
        Assert.Equal(1, c2);
        Assert.Equal(["b", "a"], Planner.RoundRobin(new[] { "a", "b" }, 7, 5).Batch); // cursor beyond the list wraps; take > count
        Assert.Empty(Planner.RoundRobin(Array.Empty<string>(), 3, 2).Batch);
    }
}
