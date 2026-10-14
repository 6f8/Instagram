using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Social;

/// <summary>Result of checking candidates one by one.</summary>
/// <param name="NotFollowingBack">Searched for in the followers list and not found: confirmed.</param>
/// <param name="Unverified">Not searched for, because the request budget ran out.</param>
/// <param name="FoundFollowing">Candidates the search found among the followers (the list read earlier missed them).</param>
public sealed record FollowBackCheck(List<StoryUser> NotFollowingBack, List<StoryUser> Unverified, int FoundFollowing, int Requests);

/// <summary>Compares the two follow lists of the logged-in account.</summary>
public static class FollowBack
{
    /// <summary>Accounts in <paramref name="following"/> that are not in <paramref name="followers"/>, in following order.</summary>
    public static List<StoryUser> NotFollowingBack(IEnumerable<StoryUser> following, IEnumerable<StoryUser> followers)
    {
        var followerIds = followers.Select(f => f.Pk).OfType<string>().ToHashSet();
        return following.Where(f => f.Pk is not null && !followerIds.Contains(f.Pk)).ToList();
    }

    /// <summary>
    /// Confirms each candidate by searching for it in the account's own followers list (what the search box of
    /// the followers dialog does). Instagram does not always return the whole followers list, so an account
    /// missing from it may still follow back; only candidates the search does not find are reported.
    /// Stops searching when <paramref name="maxRequests"/> is used up; the rest are returned as unverified.
    /// </summary>
    /// <remarks>Continues on the thread pool after its awaits, like <see cref="FollowListReader.ReadAllAsync"/>.</remarks>
    /// <param name="progress">Number of candidates checked so far.</param>
    public static async Task<FollowBackCheck> ConfirmAsync(string userId, IReadOnlyList<StoryUser> candidates,
        Func<string, CancellationToken, Task<PageResult>> fetch, Func<TimeSpan, CancellationToken, Task> delay,
        IProgress<int>? progress, CancellationToken ct, int maxRequests)
    {
        var confirmed = new List<StoryUser>();
        var unverified = new List<StoryUser>();
        var found = 0;
        var requests = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (requests >= maxRequests)
            {
                unverified.AddRange(candidates.Skip(i));
                break;
            }
            if (string.IsNullOrEmpty(c.Username))
            {
                unverified.Add(c); // nothing to search by
                continue;
            }

            await delay(FollowListReader.Pause(), ct).ConfigureAwait(false);
            var path = FollowListReader.SearchFollowersPath(userId, c.Username);
            var res = FollowListReader.Parse<FriendshipListResponse>(await fetch(path, ct).ConfigureAwait(false), path);
            requests++;
            if (res.Users?.Any(u => u.Pk == c.Pk) == true) found++;
            else confirmed.Add(c);
            progress?.Report(i + 1);
        }
        return new FollowBackCheck(confirmed, unverified, found, requests);
    }
}
