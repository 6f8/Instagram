using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cinemana.Desktop.Models;

public sealed record VideoSource(string Label, string Url, int Height)
{
    public string Container { get; init; } = "mp4";
    public bool CanDownload => Container is "mp4" or "mkv" or "ts";
    public override string ToString() => Label;
}
public sealed record SubtitleTrack(string Label, string Url, string Language)
{
    public override string ToString() => Label;
}
public sealed record PlaybackInfo(Movie Movie, List<VideoSource> Sources, List<SubtitleTrack> Subtitles)
{
    public static bool IsServiceUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("shabakaty.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".shabakaty.com", StringComparison.OrdinalIgnoreCase));

    public static PlaybackInfo Parse(JsonElement streams, JsonElement details)
    {
        if (streams.ValueKind != JsonValueKind.Array || details.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("تعذّر قراءة مصادر المشاهدة.");
        string Read(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.ToString() : "";
        var sources = new List<VideoSource>();
        foreach (var source in streams.EnumerateArray())
        {
            string url = Read(source, "videoUrl"), container = Read(source, "container"), label = Read(source, "resolution");
            if (!IsServiceUrl(url) || !new[] { "mp4", "mkv", "ts", "m3u8", "hls" }.Contains(container, StringComparer.OrdinalIgnoreCase)) continue;
            int.TryParse(Regex.Match(label, @"\d+").Value, out int height);
            sources.Add(new(label.Length > 0 ? label : container.ToUpperInvariant(), url, height) { Container = container.ToLowerInvariant() });
        }
        sources = sources.DistinctBy(s => s.Height).OrderBy(s => s.Height).ToList();
        if (sources.Count == 0) throw new InvalidDataException("لا تتوفر نسخة فيديو قابلة للتشغيل لهذا العنوان حاليًا.");
        var tracks = new List<SubtitleTrack> { new("بدون ترجمة", "", "") };
        if (details.TryGetProperty("translations", out var translations) && translations.ValueKind == JsonValueKind.Array)
        {
            var rows = translations.EnumerateArray().Where(t => IsServiceUrl(Read(t, "file")))
                .OrderBy(t => Read(t, "extention") == "srt" ? 0 : 1);
            foreach (var track in rows.DistinctBy(t => Read(t, "type")))
            {
                string lang = Read(track, "type");
                tracks.Add(new(lang switch { "ar" => "العربية", "en" => "English", _ => Read(track, "name") }, Read(track, "file"), lang));
            }
        }
        // Some episodes expose only the older translation path fields.
        foreach (var (field, lang, label) in new[] { ("arTranslationFilePath", "ar", "العربية"), ("enTranslationFilePath", "en", "English") })
        {
            string url = Read(details, field);
            if (IsServiceUrl(url) && tracks.All(t => t.Language != lang)) tracks.Add(new(label, url, lang));
        }
        return new(Movie.Parse(details), sources, tracks);
    }
}

public sealed record SubtitleCue(double Start, double End, string Text);
public static class SubtitleParser
{
    static SubtitleParser() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    /// <summary>
    /// Text of a subtitle file: UTF-8 (with or without BOM) or UTF-16 when valid, otherwise Arabic Windows-1256,
    /// the usual cause of unreadable Arabic subtitles.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))
            return new System.IO.StreamReader(new System.IO.MemoryStream(bytes), true).ReadToEnd();
        try { return new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (System.Text.DecoderFallbackException) { return System.Text.Encoding.GetEncoding(1256).GetString(bytes); }
    }
    private static readonly Regex Timing = new(@"(?<start>(?:\d+:)?\d{2}:\d{2}[,.]\d{1,3})\s*-->\s*(?<end>(?:\d+:)?\d{2}:\d{2}[,.]\d{1,3})", RegexOptions.Compiled);
    public static List<SubtitleCue> Parse(string text)
    {
        var cues = new List<SubtitleCue>();
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var match = Timing.Match(lines[i]);
            if (!match.Success) continue;
            double start = Seconds(match.Groups["start"].Value), end = Seconds(match.Groups["end"].Value);
            var content = new List<string>();
            while (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1])) content.Add(lines[++i]);
            string caption = Regex.Replace(WebUtility.HtmlDecode(string.Join("\n", content)), "<[^>]+>", "");
            if (end > start && caption.Length > 0) cues.Add(new(start, end, caption));
        }
        return cues.OrderBy(c => c.Start).ToList();
    }
    public static string ToSrt(IReadOnlyList<SubtitleCue> cues)
    {
        static string Time(double seconds)
        {
            var t = TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
        }
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < cues.Count; i++)
            text.Append(i + 1).Append("\r\n").Append(Time(cues[i].Start)).Append(" --> ").Append(Time(cues[i].End)).Append("\r\n")
                .Append(cues[i].Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
        return text.ToString();
    }
    private static double Seconds(string value)
    {
        var parts = value.Replace(',', '.').Split(':');
        return parts.Length == 3
            ? double.Parse(parts[0], CultureInfo.InvariantCulture) * 3600 + double.Parse(parts[1], CultureInfo.InvariantCulture) * 60 + double.Parse(parts[2], CultureInfo.InvariantCulture)
            : double.Parse(parts[0], CultureInfo.InvariantCulture) * 60 + double.Parse(parts[1], CultureInfo.InvariantCulture);
    }
    public static string At(IReadOnlyList<SubtitleCue> cues, double seconds)
    {
        int low = 0, high = cues.Count - 1, found = -1;
        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            if (cues[mid].Start <= seconds) { found = mid; low = mid + 1; } else high = mid - 1;
        }
        return found >= 0 && seconds < cues[found].End ? cues[found].Text : "";
    }
}
