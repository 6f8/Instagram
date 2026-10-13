namespace InstagramStoryViewer.Automation;

/// <summary>
/// A count that starts again from zero every local day (auto-watched stories, auto-likes, comments).
/// Stored as-is in the settings file; reading it never changes it, only <see cref="Add"/> does.
/// </summary>
public sealed class DailyCount
{
    /// <summary>Local date (yyyy-MM-dd) that <see cref="Count"/> belongs to.</summary>
    public string? Day { get; set; }

    public int Count { get; set; }

    private static string Key(DateTimeOffset localNow) => localNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>How many were counted today (0 when the stored count is from an earlier day).</summary>
    public int Today(DateTimeOffset localNow) => Day == Key(localNow) ? Count : 0;

    /// <summary>How many more are allowed today under <paramref name="limit"/> (never negative).</summary>
    public int Remaining(int limit, DateTimeOffset localNow) => Math.Max(0, limit - Today(localNow));

    /// <summary>Adds to today's count, rolling over to a new day first when needed.</summary>
    public void Add(int n, DateTimeOffset localNow)
    {
        if (n <= 0) return;
        var key = Key(localNow);
        if (Day != key)
        {
            Day = key;
            Count = 0;
        }
        Count += n;
    }
}
