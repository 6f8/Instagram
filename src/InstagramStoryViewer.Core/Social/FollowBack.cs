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
}
