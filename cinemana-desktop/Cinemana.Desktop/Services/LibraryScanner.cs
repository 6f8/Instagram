using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cinemana.Desktop.Models;

namespace Cinemana.Desktop.Services;

/// <summary>
/// Rebuilds "My library" from the download folder: every finished video that still has the downloader's
/// side file (.download.json, written for completed transfers) is returned as a completed item.
/// Used after reinstalling the app or moving the folder.
/// </summary>
public static class LibraryScanner
{
    private static readonly Regex EpisodeName = new(@"S(?<s>\d{1,3})E(?<e>\d{1,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static IEnumerable<DownloadSnapshot> Scan(string root, IEnumerable<string> known)
    {
        var skip = new HashSet<string>(known.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*.download.json", SearchOption.AllDirectories).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("scan", e); yield break; }
        foreach (var side in files)
        {
            string video = side[..^".download.json".Length];
            if (skip.Contains(Path.GetFullPath(video)) || !File.Exists(video)) continue;
            DownloadMetadata? meta = null;
            try { meta = JsonSerializer.Deserialize<DownloadMetadata>(File.ReadAllText(side)); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
            if (meta is not { Complete: true }) continue;
            long size = new FileInfo(video).Length;
            string stem = Path.GetFileNameWithoutExtension(video);
            var match = EpisodeName.Match(stem);
            string title = match.Success ? stem[..match.Index].Trim(' ', '-', '.') : Regex.Replace(stem, @"\s*\[\d+p\]$", "");
            var movie = new Movie(meta.MovieId, title, "", "", "", "")
            {
                ArabicTitle = title,
                Kind = match.Success ? "2" : "1",
                Season = match.Success ? int.Parse(match.Groups["s"].Value) : 0,
                Episode = match.Success ? int.Parse(match.Groups["e"].Value) : 0
            };
            string container = Path.GetExtension(video).TrimStart('.').ToLowerInvariant();
            yield return new(new(movie, meta.Quality, container, video), DownloadState.Completed, size, size, "أُعيد اكتشافه من مجلد التنزيل.");
        }
    }
}
