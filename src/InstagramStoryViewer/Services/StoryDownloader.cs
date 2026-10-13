using InstagramStoryViewer.Api;
using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Services;

public sealed class StoryDownloader
{
    private readonly InstagramClient _client;
    private readonly string _rootFolder;

    public StoryDownloader(InstagramClient client, string rootFolder)
    {
        _client = client;
        _rootFolder = rootFolder;
    }

    /// <summary>Saves one story to {root}/{username}/. Skips files that already exist.</summary>
    /// <returns>The local path of the file.</returns>
    public async Task<string> DownloadAsync(string username, StoryItem item, CancellationToken ct = default)
    {
        var url = item.BestUrl
            ?? throw new InstagramApiException(InstagramErrorKind.InvalidResponse, "لا يوجد رابط وسائط لهذا الستوري.");

        var folder = Path.Combine(_rootFolder, Sanitize(username));
        Directory.CreateDirectory(folder);

        var ext = item.IsVideo ? ".mp4" : ".jpg";
        var name = $"{Sanitize(username)}_{item.TakenAtTime.ToLocalTime():yyyyMMdd_HHmmss}_{item.Pk ?? item.Id}{ext}";
        var path = Path.Combine(folder, name);

        if (!File.Exists(path))
            await _client.DownloadAsync(url, path, ct);

        return path;
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
