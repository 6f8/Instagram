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
        IProgress<int>? progress, CancellationToken ct, int maxRequests, IReadOnlyList<StoryUser>? knownFollowers = null)
    {
        var confirmed = new List<StoryUser>();
        var unverified = new List<StoryUser>();
        var pending = new List<StoryUser>(); // not found, waiting for the next control search to pass
        var found = 0;
        var requests = 0;
        var controls = (knownFollowers ?? []).Where(f => !string.IsNullOrEmpty(f.Username) && f.Pk is not null).ToList();
        var nextControl = 0;

        async Task<bool> SearchFindsAsync(StoryUser u)
        {
            await delay(FollowListReader.Pause(), ct).ConfigureAwait(false);
            var path = FollowListReader.SearchFollowersPath(userId, u.Username!);
            var res = FollowListReader.Parse<FriendshipListResponse>(await fetch(path, ct).ConfigureAwait(false), path);
            requests++;
            if (res.Users is null)
                throw new Api.InstagramApiException(Api.InstagramErrorKind.InvalidResponse, "ردّ بحث بلا قائمة حسابات.") { RequestPath = path };
            return res.Users.Any(x => x.Pk == u.Pk);
        }

        // A control search for someone known to follow must find them; otherwise the search is not working
        // (soft limit, changed endpoint) and "not found" proves nothing.
        async Task<bool> ControlPassesAsync()
        {
            if (controls.Count == 0) return true;
            return await SearchFindsAsync(controls[nextControl++ % controls.Count]).ConfigureAwait(false);
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (requests >= maxRequests - (controls.Count > 0 ? 1 : 0) || string.IsNullOrEmpty(c.Username))
            {
                if (string.IsNullOrEmpty(c.Username)) { unverified.Add(c); continue; }
                unverified.AddRange(pending); pending.Clear();
                unverified.AddRange(candidates.Skip(i));
                return new FollowBackCheck(confirmed, unverified, found, requests);
            }
            if (await SearchFindsAsync(c).ConfigureAwait(false)) found++;
            else if (controls.Count == 0) confirmed.Add(c);
            else pending.Add(c);
            progress?.Report(i + 1);

            if (pending.Count >= ControlEvery || i == candidates.Count - 1)
            {
                if (!await ControlPassesAsync().ConfigureAwait(false))
                {
                    unverified.AddRange(pending);
                    unverified.AddRange(candidates.Skip(i + 1));
                    return new FollowBackCheck(confirmed, unverified, found, requests);
                }
                confirmed.AddRange(pending);
                pending.Clear();
            }
        }
        confirmed.AddRange(pending);
        return new FollowBackCheck(confirmed, unverified, found, requests);
    }

    /// <summary>Not-found candidates are only confirmed after a control search passes, at least this often.</summary>
    public const int ControlEvery = 5;
}
