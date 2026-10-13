using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using InstagramStoryViewer.Diagnostics;

namespace InstagramStoryViewer.Gui.Services;

/// <summary>
/// Profile pictures and thumbnails saved on disk at the size they are shown, so the next refresh or the next
/// start shows them at once instead of downloading them again.
/// Files expire after <see cref="MaxAge"/>; the folder is kept under <see cref="MaxBytes"/>.
/// </summary>
public static class ImageDiskCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public const long MaxBytes = 150L * 1024 * 1024;

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InstagramStoryViewer", "cache", "images");

    /// <summary>
    /// Cache file for a remote image at a given decode width. Instagram's CDN links carry signatures in the
    /// query string that change on every response, so only the path (which changes with the picture) is used.
    /// </summary>
    public static string PathFor(Uri uri, int width)
    {
        var key = $"{width}|{uri.Host}{uri.AbsolutePath}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return Path.Combine(Folder, hash + ".jpg");
    }

    /// <summary>A fresh cached copy, or null.</summary>
    public static string? TryGet(Uri uri, int width)
    {
        var path = PathFor(uri, width);
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0 && DateTime.UtcNow - info.LastWriteTimeUtc < MaxAge ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Stores a decoded image (must be called on the image's thread; the file is written in the background).</summary>
    public static void Store(Uri uri, int width, BitmapSource image)
    {
        byte[] bytes;
        try
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            bytes = ms.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return;
        }

        var path = PathFor(uri, width);
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a missing cache entry only means the image is downloaded again next time
            }
        });
    }

    /// <summary>Deletes expired files and the oldest ones beyond the size limit (run once at start-up).</summary>
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var files = new DirectoryInfo(Folder).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            var removed = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (DateTime.UtcNow - f.LastWriteTimeUtc < MaxAge && total <= MaxBytes) continue;
                try { f.Delete(); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (removed > 0) AppLog.Info($"Image cache: removed {removed} old files");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Image cache cleanup failed: {ex.Message}");
        }
    }
}
