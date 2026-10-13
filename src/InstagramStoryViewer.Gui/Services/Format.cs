using System.Globalization;

namespace InstagramStoryViewer.Gui.Services;

/// <summary>Arabic, human-friendly formatting for times and sizes.</summary>
public static class Format
{
    public static string Ago(DateTimeOffset time)
    {
        var span = DateTimeOffset.UtcNow - time;
        if (span.TotalMinutes < 1) return "الآن";
        if (span.TotalHours < 1) return $"منذ {(int)span.TotalMinutes} د";
        if (span.TotalDays < 1) return $"منذ {(int)span.TotalHours} س";
        return $"منذ {(int)span.TotalDays} يوم";
    }

    public static string Remaining(DateTimeOffset until)
    {
        var span = until - DateTimeOffset.UtcNow;
        if (span <= TimeSpan.Zero) return "منتهي";
        if (span.TotalHours < 1) return $"{Math.Max(1, (int)span.TotalMinutes)} د";
        return $"{(int)span.TotalHours} س";
    }

    public static string Duration(double? seconds)
    {
        if (seconds is null or <= 0) return "";
        var t = TimeSpan.FromSeconds(Math.Round(seconds.Value));
        return t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{v:0.#} {units[i]}");
    }
}
