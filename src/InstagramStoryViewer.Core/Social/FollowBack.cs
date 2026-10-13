using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Social;

/// <summary>Compares the two follow lists of the logged-in account.</summary>
public static class FollowBack
{
    /// <summary>Accounts in <paramref name="following"/> that are not in <paramref name="followers"/>, in following order.</summary>
    public static List<StoryUser> NotFollowingBack(IEnumerable<StoryUser> following, IEnumerable<StoryUser> followers)
    {
        var followerIds = followers.Select(f => f.Pk).OfType<string>().ToHashSet();
        return following.Where(f => f.Pk is not null && !followerIds.Contains(f.Pk)).ToList();
    }

    /// <summary>Requests needed to read both lists (200 accounts per page, at least one page each).</summary>
    public static int EstimateRequests(int following, int followers) =>
        Math.Max(1, (following + 199) / 200) + Math.Max(1, (followers + 199) / 200);
}
