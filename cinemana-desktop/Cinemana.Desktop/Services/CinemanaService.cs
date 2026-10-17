using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cinemana.Desktop.Models;

namespace Cinemana.Desktop.Services;

public sealed class CinemanaService : IDisposable
{
    private readonly HttpClient client;
    private readonly string cacheDir;
    private readonly SemaphoreSlim imageSlots = new(4);
    public CinemanaService(string root, HttpClient? client = null)
    {
        this.client = client ?? new HttpClient { BaseAddress = new Uri("https://cinemana.shabakaty.com/api/android/"), Timeout = TimeSpan.FromSeconds(18) };
        cacheDir = Path.Combine(root, "Cache"); Directory.CreateDirectory(cacheDir);
    }
    public static string SearchKey(string query) => Convert.ToBase64String(Encoding.UTF8.GetBytes(query)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool StorageError(Exception e) => e is IOException or UnauthorizedAccessException;
    private async Task<CacheResult<JsonElement>> Read(string path, bool refresh, CancellationToken ct)
    {
        string file = Path.Combine(cacheDir, Hash(path) + ".json");
        JsonElement? cached = null; DateTimeOffset? time = null;
        try
        {
            if (File.Exists(file)) { using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct)); cached = doc.RootElement.Clone(); time = File.GetLastWriteTimeUtc(file); }
        }
        catch (Exception e) when (StorageError(e) || e is JsonException) { }
        if (!refresh && cached.HasValue && DateTimeOffset.UtcNow - time < TimeSpan.FromMinutes(15)) return new(cached.Value, false, time);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var response = await client.GetAsync(path, ct);
                    if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt == 0)
                    { await Task.Delay(700, ct); continue; }
                    response.EnsureSuccessStatusCode();
                    string body = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) throw new InvalidDataException("استجابة غير صالحة من الخدمة");
                    // Replace atomically so a cancelled request never leaves a partial JSON cache.
                    try { var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp"; await File.WriteAllTextAsync(temp, body, ct); File.Move(temp, file, true); }
                    catch (Exception e) when (StorageError(e)) { }
                    return new(doc.RootElement.Clone(), false, DateTimeOffset.UtcNow);
                }
                catch (HttpRequestException e) when (attempt == 0 && e.StatusCode is null) { await Task.Delay(700, ct); }
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested && (e is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException))
        { if (cached.HasValue) return new(cached.Value, true, time); throw; }
    }
    public async Task<CacheResult<List<Movie>>> Catalog(string section, int page, string query, bool refresh, bool images, CancellationToken ct)
    {
        string path = section switch
        {
            "series" => $"latestSeries/level/0/itemsPerPage/24/page/{page}/",
            "search" => $"video/V/2/itemsPerPage/24/video_title_search/{SearchKey(query)}/pageNumber/{page}/level/0",
            _ => $"latestMovies/level/0/itemsPerPage/24/page/{page}/"
        };
        var result = await Read(path, refresh, ct);
        if (result.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("شكل قائمة العناوين غير متوقع");
        var movies = result.Value.EnumerateArray().Select(Movie.Parse).Where(m => m.Id.Length > 0).ToList();
        if (images) movies = (await Task.WhenAll(movies.Select(m => WithImage(m, ct)))).ToList();
        return new(movies, result.IsStale, result.SavedAt);
    }
    public async Task<Movie> Details(string id, CancellationToken ct)
    {
        var result = await Read($"allVideoInfo/id/{Uri.EscapeDataString(id)}", false, ct);
        if (result.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("تفاصيل غير صالحة");
        return Movie.Parse(result.Value);
    }
    public async Task<List<Movie>> Episodes(string id, CancellationToken ct)
    {
        var result = await Read($"videoSeason/id/{Uri.EscapeDataString(id)}", false, ct);
        if (result.Value.ValueKind != JsonValueKind.Array) return [];
        return result.Value.EnumerateArray().Select(Movie.Parse).Where(m => m.Id.Length > 0).OrderBy(m => m.Season).ThenBy(m => m.Episode).ToList();
    }
    private async Task<JsonElement> FreshJson(string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.Clone();
    }
    public async Task<PlaybackInfo> Playback(string id, CancellationToken ct)
    {
        // Stream URLs are signed and short-lived; never persist or reuse them from the catalog cache.
        string key = Uri.EscapeDataString(id);
        var streams = FreshJson($"transcoddedFiles/id/{key}", ct);
        var details = FreshJson($"allVideoInfo/id/{key}", ct);
        await Task.WhenAll(streams, details);
        return PlaybackInfo.Parse(await streams, await details);
    }
    public async Task<List<SubtitleCue>> Subtitles(SubtitleTrack track, CancellationToken ct)
    {
        if (track.Url.Length == 0) return [];
        if (!PlaybackInfo.IsServiceUrl(track.Url)) throw new InvalidDataException("مصدر الترجمة غير صالح.");
        using var response = await client.GetAsync(track.Url, ct);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 2_000_000) throw new InvalidDataException("ملف الترجمة أكبر من الحد المدعوم.");
        return SubtitleParser.Parse(body);
    }
    public async Task<Movie> WithImage(Movie movie, CancellationToken ct)
    {
        if (!Uri.TryCreate(movie.Poster, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.EndsWith(".shabakaty.com", StringComparison.OrdinalIgnoreCase)) return movie;
        string file = Path.Combine(cacheDir, Hash(movie.Poster) + ".img");
        if (File.Exists(file)) return movie with { LocalPoster = file };
        await imageSlots.WaitAsync(ct);
        try
        {
            if (!File.Exists(file))
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/") != true || response.Content.Headers.ContentLength > 10_000_000) return movie;
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > 10_000_000) return movie;
                string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes, ct); File.Move(temp, file, true);
            }
            return movie with { LocalPoster = file };
        }
        catch (Exception e) when (!ct.IsCancellationRequested && (StorageError(e) || e is HttpRequestException or TaskCanceledException)) { return movie; }
        finally { imageSlots.Release(); }
    }
    /// <summary>
    /// One small uncached request at start-up. Null when the service answers in the shape the app expects;
    /// otherwise a message that says whether the network is down or the site's format changed.
    /// </summary>
    public async Task<string?> Health(CancellationToken ct)
    {
        try
        {
            var list = await FreshJson("latestMovies/level/0/itemsPerPage/1/page/0/", ct);
            if (list.ValueKind != JsonValueKind.Array) return Changed();
            var first = list.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("nb", out _)) return Changed();
            return null;
        }
        catch (JsonException) { return Changed(); }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) { return Changed(); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException)
        { Log.Error("health", e); return "تعذّر الوصول إلى سينمانا. تحقق من الاتصال؛ يمكنك فتح «مكتبتي» للمشاهدة بدون إنترنت."; }
        static string Changed() { Log.Info("health: unexpected service format"); return "يبدو أن موقع سينمانا غيّر طريقة عمله. قد لا تعمل بعض الأقسام حتى يصدر تحديث للتطبيق."; }
    }
    public void Dispose() => client.Dispose();
}

