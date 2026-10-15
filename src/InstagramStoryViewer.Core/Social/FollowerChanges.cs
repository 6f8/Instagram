namespace InstagramStoryViewer.Social;

/// <summary>A follower saved with a check, to compare with the next one.</summary>
public sealed record FollowerRef(string Pk, string Username, string? FullName = null, string? ProfilePicUrl = null);

/// <summary>Who stopped following and who started, between two checks.</summary>
public static class FollowerChanges
{
    /// <summary>
    /// Followers in <paramref name="before"/> but not in <paramref name="now"/> (lost) and the other way round (gained).
    /// "Lost" ones must still be confirmed with a followers search: the list Instagram returns can be incomplete.
    /// </summary>
    public static (List<FollowerRef> Lost, List<FollowerRef> Gained) Compare(IEnumerable<FollowerRef> before, IEnumerable<FollowerRef> now)
    {
        var b = before.GroupBy(f => f.Pk).ToDictionary(g => g.Key, g => g.First());
        var n = now.GroupBy(f => f.Pk).ToDictionary(g => g.Key, g => g.First());
        return (b.Values.Where(f => !n.ContainsKey(f.Pk)).ToList(), n.Values.Where(f => !b.ContainsKey(f.Pk)).ToList());
    }
}
