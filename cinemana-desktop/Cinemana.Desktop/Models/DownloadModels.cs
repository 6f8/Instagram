using System.IO;
using System.Text.RegularExpressions;

namespace Cinemana.Desktop.Models;

public enum DownloadState { Queued, Running, Paused, Completed, Failed, Cancelled, Unavailable }
// SubtitleLanguage: "ar", "en" or "" (no subtitle). Saved as an .srt beside the video with the same name.
public sealed record DownloadSpec(Movie Movie, int Quality, string Container, string Destination, string SubtitleLanguage = "")
{
    public string Key => Movie.Id + ":" + Quality + ":" + Path.GetFullPath(Destination).ToUpperInvariant();
}
public sealed record DownloadSnapshot(DownloadSpec Spec, DownloadState State, long Bytes = 0, long? Total = null, string Message = "");
public sealed record DownloadProgress(long Bytes, long? Total, double BytesPerSecond);
public sealed record DownloadResult(long Bytes, bool AlreadyComplete = false, string SubtitleNote = "");
public sealed record DownloadPlan(List<DownloadSpec> Files, List<string> Notices);
public sealed record QualityChoice(int Height, string Label) { public override string ToString() => Label; }
public sealed record DownloadMetadata(string MovieId, int Quality, string SourcePath, long? Total, string? ETag, DateTimeOffset? LastModified, bool Complete = false);

public static class DownloadPaths
{
    public static string SafeName(string value)
    {
        string name = Regex.Replace(value, "[\\x00-\\x1F<>:\"/\\\\|?*]", "_").Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name)) name = "Cinemana";
        if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) name = "_" + name;
        return name.Length > 70 ? name[..70].TrimEnd('.', ' ') : name;
    }
    /// <summary>
    /// Media-server style names that VLC, Plex, Jellyfin and TVs recognise:
    /// مسلسلات/Title/الموسم 01/Title - S01E04.mp4 and أفلام/Title (2023)/Title (2023).mp4.
    /// The quality is added to the name only when several qualities of one title are downloaded.
    /// </summary>
    public static string FilePath(string root, string title, string groupId, Movie movie, VideoSource source, bool withQuality = false)
    {
        string name = SafeName(title);
        string extension = source.Container switch { "mkv" => ".mkv", "ts" => ".ts", _ => ".mp4" };
        string suffix = withQuality ? $" [{source.Height}p]" : "";
        string folder, file;
        if (movie.Kind == "2")
        {
            folder = Path.Combine(Path.GetFullPath(root), "مسلسلات", name, $"الموسم {movie.Season:00}");
            file = $"{name} - S{movie.Season:00}E{movie.Episode:00}{suffix}{extension}";
        }
        else
        {
            string titled = movie.Year.Length == 4 ? $"{name} ({movie.Year})" : name;
            folder = Path.Combine(Path.GetFullPath(root), "أفلام", titled);
            file = titled + suffix + extension;
        }
        _ = groupId; // kept for callers; names no longer carry service ids
        string full = Path.GetFullPath(Path.Combine(folder, file));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("مسار التنزيل غير صالح.");
        return full;
    }
    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{bytes / 1024d:0.0} KB";
}
