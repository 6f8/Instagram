using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Cinemana.Desktop.Models;

namespace Cinemana.Desktop.Services;

public sealed class FileDownloader(HttpClient client, Func<Movie, CancellationToken, Task<PlaybackInfo>> resolve)
{
    // Bytes per second allowed for one transfer right now; 0 or less means unlimited.
    public Func<long> SpeedLimit { get; init; } = () => 0;
    public async Task<DownloadResult> Download(DownloadSpec spec, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        string file = Path.GetFullPath(spec.Destination), partial = file + ".part", metaFile = file + ".download.json";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        DownloadMetadata? previous = null;
        try { if (File.Exists(metaFile)) previous = JsonSerializer.Deserialize<DownloadMetadata>(await File.ReadAllTextAsync(metaFile, ct)); }
        catch (JsonException) { throw new InvalidDataException("بيانات الملف غير المكتمل تالفة. اختر مجلدًا آخر."); }
        bool owned = previous is not null && previous.MovieId == spec.Movie.Id && previous.Quality == spec.Quality;
        if (File.Exists(file))
        {
            if (owned && previous!.Complete && previous.Total == new FileInfo(file).Length)
            {
                progress?.Report(new(previous.Total.Value, previous.Total, 0));
                string note = spec.SubtitleLanguage.Split('+', StringSplitOptions.RemoveEmptyEntries).Any(l => !File.Exists(SubtitlePath(file, l))) ? await SaveSubtitle(spec, await resolve(spec.Movie, ct), file, ct) : "";
                return new(previous.Total.Value, true, note);
            }
            throw new IOException("يوجد ملف بهذا الاسم. لن يُستبدل؛ اختر مجلدًا آخر.");
        }
        long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (File.Exists(partial) && !owned) throw new InvalidDataException("يوجد ملف جزئي غير معروف. اختر مجلدًا آخر.");
        var playback = await resolve(spec.Movie, ct);
        var source = playback.Sources.FirstOrDefault(s => s.Height == spec.Quality && s.CanDownload && s.Container == spec.Container)
            ?? throw new QualityUnavailableException();
        if (!PlaybackInfo.IsServiceUrl(source.Url)) throw new InvalidDataException("مصدر تنزيل غير صالح.");
        string sourcePath = new Uri(source.Url).GetLeftPart(UriPartial.Path);
        if (existing > 0 && previous!.SourcePath != sourcePath) throw new InvalidDataException("تغيّر ملف الفيديو في الخدمة. اختر مجلدًا جديدًا لإعادة تنزيله.");
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
            if (EntityTagHeaderValue.TryParse(previous!.ETag, out var tag) && !tag.IsWeak) request.Headers.IfRange = new RangeConditionHeaderValue(tag);
            else if (previous.LastModified.HasValue) request.Headers.IfRange = new RangeConditionHeaderValue(previous.LastModified.Value);
        }
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
        headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
        if (response.RequestMessage?.RequestUri is { } finalUri && !PlaybackInfo.IsServiceUrl(finalUri.AbsoluteUri)) throw new InvalidDataException("مصدر التنزيل أعاد توجيهًا غير صالح.");
        var range = response.Content.Headers.ContentRange;
        bool sameValidator = previous?.ETag is null || response.Headers.ETag is null || previous.ETag == response.Headers.ETag.ToString();
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0 && previous?.Total == existing && range?.Length == existing && sameValidator)
        {
            await SaveMetadata(metaFile, previous with { Complete = true }, ct);
            File.Move(partial, file, false);
            progress?.Report(new(existing, existing, 0)); return new(existing);
        }
        response.EnsureSuccessStatusCode();
        string type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!(type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || type is "application/octet-stream" or "binary/octet-stream" or "application/x-matroska"))
            throw new InvalidDataException("لم تُرجع الخدمة ملف فيديو صالحًا.");
        long offset;
        long? total;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (range?.Unit != "bytes" || range.From != existing || range.Length is null || range.To < range.From || range.To >= range.Length)
                throw new InvalidDataException("استجابة الاستئناف غير مطابقة للملف الجزئي.");
            if (response.Content.Headers.ContentLength is { } partLength && partLength != range.To - range.From + 1)
                throw new InvalidDataException("طول الجزء المستأنف غير مطابق للاستجابة.");
            if (previous?.Total is { } expected && existing > 0 && expected != range.Length)
                throw new InvalidDataException("حجم الملف في الخدمة تغيّر. اختر مجلدًا جديدًا.");
            if (existing > 0 && previous?.ETag is { } oldTag && response.Headers.ETag is { } newTag && oldTag != newTag.ToString())
                throw new InvalidDataException("تغيّر ملف الفيديو أثناء الاستئناف.");
            offset = existing; total = range.Length;
        }
        else if (response.StatusCode == HttpStatusCode.OK) { offset = 0; total = response.Content.Headers.ContentLength; }
        else throw new InvalidDataException("استجابة تنزيل غير متوقعة.");
        if (total is <= 0) throw new InvalidDataException("ملف الفيديو فارغ.");
        var metadata = new DownloadMetadata(spec.Movie.Id, spec.Quality, sourcePath, total, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
        await SaveMetadata(metaFile, metadata, ct);
        long bytes = offset, initial = offset;
        var clock = Stopwatch.StartNew(); var lastReport = TimeSpan.Zero;
        progress?.Report(new(bytes, total, 0));
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 131072, FileOptions.Asynchronous))
        {
            byte[] buffer = new byte[131072];
            while (true)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                int read = await input.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                bytes += read;
                if (total.HasValue && bytes > total) throw new InvalidDataException("حجم البيانات يتجاوز الحجم المعلن.");
                long limit = SpeedLimit();
                if (limit > 0)
                {
                    // Sleep until the average rate since the transfer started is back under the limit.
                    double ahead = (bytes - initial) / (double)limit - clock.Elapsed.TotalSeconds;
                    if (ahead > 0.02) await Task.Delay(TimeSpan.FromSeconds(Math.Min(ahead, 2)), ct).ConfigureAwait(false);
                }
                if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(250))
                { lastReport = clock.Elapsed; progress?.Report(new(bytes, total, (bytes - initial) / Math.Max(0.01, clock.Elapsed.TotalSeconds))); }
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        if (bytes == 0 || total.HasValue && bytes != total) throw new IOException("انقطع الملف قبل اكتماله. يمكن متابعة التنزيل.");
        // Publish only a complete file; never overwrite an existing final file.
        await SaveMetadata(metaFile, metadata with { Total = bytes, Complete = true }, ct);
        File.Move(partial, file, false);
        progress?.Report(new(bytes, bytes, 0));
        return new(bytes, false, await SaveSubtitle(spec, playback, file, ct));
    }
    /// <summary>"Title - S01E04.ar.srt" beside "Title - S01E04.mp4" (language-tagged, as media players expect).</summary>
    public static string SubtitlePath(string videoFile, string language) => Path.ChangeExtension(videoFile, (language.Length > 0 ? "." + language : "") + ".srt");
    /// <summary>Subtitles saved beside a video: new ".ar.srt" names first, then the older plain ".srt".</summary>
    public static IEnumerable<string> SubtitlesBeside(string videoFile)
    {
        string folder = Path.GetDirectoryName(videoFile)!, stem = Path.GetFileNameWithoutExtension(videoFile);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, stem + "*.srt").Where(f => Path.GetFileNameWithoutExtension(f) == stem || Path.GetFileName(f).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f).Contains(".ar.", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
    }
    // Writes each chosen subtitle language ("ar", "en" or "ar+en") as UTF-8 SRT named after the video, e.g.
    // "Title - S01E04.ar.srt", so players load it automatically. A subtitle problem never fails the video.
    private async Task<string> SaveSubtitle(DownloadSpec spec, PlaybackInfo playback, string videoFile, CancellationToken ct)
    {
        var notes = new List<string>();
        foreach (string language in spec.SubtitleLanguage.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string target = SubtitlePath(videoFile, language);
            if (File.Exists(target)) continue;
            var track = playback.Subtitles.FirstOrDefault(t => t.Language == language && t.Url.Length > 0);
            if (track is null) { notes.Add(language == "ar" ? "لا تتوفر ترجمة عربية." : "لا تتوفر ترجمة إنجليزية."); continue; }
            try
            {
                if (!PlaybackInfo.IsServiceUrl(track.Url)) { notes.Add("مصدر الترجمة غير صالح."); continue; }
                using var response = await client.GetAsync(track.Url, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                byte[] raw = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (raw.Length > 4_000_000) { notes.Add("ملف الترجمة أكبر من الحد المدعوم."); continue; }
                var cues = SubtitleParser.Parse(SubtitleParser.Decode(raw));
                if (cues.Count == 0) { notes.Add("ملف الترجمة فارغ أو غير مدعوم."); continue; }
                string temp = target + ".tmp";
                await File.WriteAllTextAsync(temp, SubtitleParser.ToSrt(cues), new System.Text.UTF8Encoding(true), ct).ConfigureAwait(false);
                File.Move(temp, target, false);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            { Log.Error("subtitle", e); notes.Add("تعذّر حفظ ملف الترجمة."); }
        }
        return string.Join(" ", notes);
    }
    private static async Task SaveMetadata(string path, DownloadMetadata value, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(value), ct).ConfigureAwait(false);
        File.Move(path + ".tmp", path, true);
    }
}
public sealed class QualityUnavailableException : IOException
{
    public QualityUnavailableException() : base("الجودة المطلوبة لم تعد متاحة لهذا العنوان.") { }
}
