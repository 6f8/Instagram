using Cinemana.Desktop.Models;

namespace Cinemana.Desktop.Services;

public sealed class DownloadPlanner(Func<Movie, CancellationToken, Task<PlaybackInfo>> resolve, Func<string, CancellationToken, Task<long?>>? sizeOf = null)
{
    // -1 means every quality available for each episode; -2 means its best quality.
    public async Task<DownloadPlan> Build(IReadOnlyList<Movie> movies, string title, string groupId, string directory, int quality, string subtitleLanguage, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var files = new List<DownloadSpec>(); var notices = new List<string>(); var sizes = new List<Task<long?>>();
        int count = 0;
        foreach (var movie in movies.DistinctBy(m => m.Id))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var media = await resolve(movie, ct);
                var available = media.Sources.Where(s => s.CanDownload).OrderBy(s => s.Height).ToList();
                var selected = quality switch { -1 => available, -2 => available.TakeLast(1).ToList(), _ => available.Where(s => s.Height == quality).ToList() };
                if (selected.Count == 0) notices.Add($"{movie.Title} {movie.EpisodeLabel}: الجودة المطلوبة غير متاحة للتنزيل.");
                foreach (var source in selected)
                {
                    if (sizeOf is not null) sizes.Add(sizeOf(source.Url, ct));
                    files.Add(new(movie, source.Height, source.Container, DownloadPaths.FilePath(directory, title, groupId, movie, source, withQuality: quality == -1), subtitleLanguage));
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or System.IO.InvalidDataException)
            { notices.Add($"{movie.Title} {movie.EpisodeLabel}: تعذّر جلب الجودات المتاحة."); }
            progress?.Report((++count, movies.Count));
        }
        var known = await Task.WhenAll(sizes);
        return new(files.DistinctBy(f => f.Key).ToList(), notices) { KnownBytes = known.Sum(k => k ?? 0), UnknownSizes = sizeOf is null ? files.Count : known.Count(k => k is null) };
    }
}
