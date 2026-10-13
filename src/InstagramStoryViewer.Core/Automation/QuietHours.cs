namespace InstagramStoryViewer.Automation;

/// <summary>Local hours during which nothing runs automatically.</summary>
public static class QuietHours
{
    /// <summary>True when <paramref name="hour"/> falls in [from, to), wrapping past midnight (e.g. 23 → 7).</summary>
    public static bool Contains(bool enabled, int from, int to, int hour)
    {
        if (!enabled || from == to) return false;
        return from < to ? hour >= from && hour < to : hour >= from || hour < to;
    }
}
