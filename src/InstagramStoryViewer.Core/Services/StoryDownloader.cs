using InstagramStoryViewer.Api;
using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Services;

public sealed class StoryDownloader
{
    private readonly InstagramClient _client;
    public string RootFolder { get; set; }

    public StoryDownloader(InstagramClient client, string rootFolder)
    {
        _client = client;
        RootFolder = rootFolder;
    }

    /// <summary>
    /// Where a new download of this story is saved. The timestamp is UTC so the name does not change with
    /// the machine's time zone or daylight saving (which used to cause duplicate downloads).
    /// </summary>
    public string GetPath(string username, StoryItem item)
    {
        var name = $"{Sanitize(username)}_{item.TakenAtTime.UtcDateTime:yyyyMMdd_HHmmss}Z_{MediaKey(item)}{Extension(item)}";
        return Path.Combine(RootFolder, Sanitize(username), name);
    }

    /// <summary>
    /// An already-downloaded copy of this story, found by its media id (so files saved by older versions with
    /// local-time names are recognised too). Null when it has not been downloaded.
    /// </summary>
    public string? FindExistingPath(string username, StoryItem item)
    {
        var folder = Path.Combine(RootFolder, Sanitize(username));
        if (!Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFiles(folder, $"*_{MediaKey(item)}{Extension(item)}").FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Saves one story to {root}/{username}/. Skips stories that were already downloaded.</summary>
    /// <returns>The local path of the file.</returns>
    public async Task<string> DownloadAsync(string username, StoryItem item, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var existing = FindExistingPath(username, item);
        if (existing is not null) return existing;

        var url = item.BestUrl
            ?? throw new InstagramApiException(InstagramErrorKind.InvalidResponse, "لا يوجد رابط وسائط لهذا الستوري.");

        var path = GetPath(username, item);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await _client.DownloadAsync(url, path, ct, progress).ConfigureAwait(false);
        return path;
    }

    private static string MediaKey(StoryItem item) => Sanitize(item.Pk ?? item.Id ?? "unknown");

    private static string Extension(StoryItem item) => item.IsVideo ? ".mp4" : ".jpg";

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
