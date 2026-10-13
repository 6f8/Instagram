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

    /// <summary>Where <see cref="DownloadAsync"/> saves (or has saved) this story.</summary>
    public string GetPath(string username, StoryItem item)
    {
        var ext = item.IsVideo ? ".mp4" : ".jpg";
        var name = $"{Sanitize(username)}_{item.TakenAtTime.ToLocalTime():yyyyMMdd_HHmmss}_{item.Pk ?? item.Id}{ext}";
        return Path.Combine(RootFolder, Sanitize(username), name);
    }

    /// <summary>Saves one story to {root}/{username}/. Skips files that already exist.</summary>
    /// <returns>The local path of the file.</returns>
    public async Task<string> DownloadAsync(string username, StoryItem item, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var url = item.BestUrl
            ?? throw new InstagramApiException(InstagramErrorKind.InvalidResponse, "لا يوجد رابط وسائط لهذا الستوري.");

        var path = GetPath(username, item);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
            await _client.DownloadAsync(url, path, ct, progress);

        return path;
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
