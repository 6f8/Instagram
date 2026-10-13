using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Automation;

/// <summary>An account the automation may act on (from the stories tray or the favorites).</summary>
public sealed record AccountTarget(string UserId, string Username);

/// <summary>One story or post the automation will open.</summary>
public sealed record PlannedItem(AccountTarget Account, string MediaPk, string Url, StoryItem? Story = null, bool IsPost = false);

/// <summary>
/// Decides what the automation does in a round, from data already fetched. Pure functions, so the rules
/// (only unseen items, never twice, within the budget, in a stable order) are covered by tests.
/// </summary>
public static class Planner
{
    public static string StoryUrl(string username, StoryItem item) =>
        $"https://www.instagram.com/stories/{Uri.EscapeDataString(username)}/{item.Pk ?? item.Id?.Split('_')[0]}/";

    private static string Key(StoryItem item) => item.Pk ?? item.Id ?? "";

    /// <summary>Stories newer than each reel's "seen" marker, oldest first per account, capped at <paramref name="budget"/>.</summary>
    public static List<PlannedItem> UnseenStories(IEnumerable<Reel> reels, IReadOnlyCollection<AccountTarget> accounts, int budget)
    {
        if (budget <= 0) return new List<PlannedItem>();
        var byId = accounts.ToDictionary(a => a.UserId);
        var result = new List<PlannedItem>();
        foreach (var reel in reels)
        {
            if (!byId.TryGetValue(reel.User?.Pk ?? reel.Id ?? "", out var account) || reel.Items is null) continue;
            var seen = reel.Seen ?? 0;
            foreach (var item in reel.Items.Where(i => i.TakenAt > seen).OrderBy(i => i.TakenAt))
            {
                if (Key(item).Length == 0) continue;
                result.Add(new PlannedItem(account, Key(item), StoryUrl(account.Username, item), item));
                if (result.Count == budget) return result;
            }
        }
        return result;
    }

    /// <summary>Unseen stories that are not liked yet and were not handled before.</summary>
    public static List<PlannedItem> StoriesToLike(IEnumerable<Reel> reels, IReadOnlyCollection<AccountTarget> accounts,
        Func<string, bool> alreadyHandled, int budget) =>
        UnseenStories(reels, accounts, int.MaxValue)
            .Where(p => p.Story!.HasLiked != true && !alreadyHandled(p.MediaPk))
            .Take(Math.Max(0, budget))
            .ToList();

    /// <summary>Recent posts that are not liked yet and were not handled before.</summary>
    public static List<PlannedItem> PostsToLike(AccountTarget account, IEnumerable<FeedPost> posts, Func<string, bool> alreadyHandled,
        TimeSpan maxAge, DateTimeOffset now) =>
        posts.Where(p => p.HasLiked != true && p.Pk is not null && p.Url is not null && !alreadyHandled(p.Pk)
                         && now - DateTimeOffset.FromUnixTimeSeconds(p.TakenAt) <= maxAge)
             .Select(p => new PlannedItem(account, p.Pk!, p.Url!, IsPost: true))
             .ToList();

    /// <summary>The next <paramref name="take"/> accounts in a round-robin over <paramref name="all"/>, and the new cursor.</summary>
    public static (List<T> Batch, int NextCursor) RoundRobin<T>(IReadOnlyList<T> all, int cursor, int take)
    {
        if (all.Count == 0 || take <= 0) return (new List<T>(), 0);
        take = Math.Min(take, all.Count);
        var start = ((cursor % all.Count) + all.Count) % all.Count;
        var batch = Enumerable.Range(0, take).Select(k => all[(start + k) % all.Count]).ToList();
        return (batch, (start + take) % all.Count);
    }
}
