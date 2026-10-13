using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using InstagramStoryViewer.Diagnostics;

namespace InstagramStoryViewer.Gui.Services;

/// <param name="ExeUrl">Download link of InstagramStoryViewer.exe in the release (null if missing).</param>
/// <param name="ShaUrl">Download link of the .sha256 file published next to it.</param>
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? ExeUrl = null, string? ShaUrl = null)
{
    public bool CanInstall => ExeUrl is not null && ShaUrl is not null;
}

/// <summary>Asks GitHub for the latest release, and installs it in place.</summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/6f8/Instagram/releases/latest";
    public const string ReleasesPage = "https://github.com/6f8/Instagram/releases/latest";
    private const string ExeName = "InstagramStoryViewer.exe";

    private static HttpClient CreateHttp(Version current, TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("InstagramStoryViewer", current.ToString(3)));
        return http;
    }

    /// <summary>The newer release, or null when up to date / offline / no release yet.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var http = CreateHttp(current, TimeSpan.FromSeconds(15));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi, ct));
            return Parse(doc.RootElement, current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static UpdateInfo? Parse(JsonElement release, Version current)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        var url = release.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || latest <= current) return null;

        string? exe = null, sha = null;
        if (release.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var link = a.GetProperty("browser_download_url").GetString();
                if (name == ExeName) exe = link;
                else if (name == ExeName + ".sha256") sha = link;
            }
        }
        return new UpdateInfo(latest, tag, url, exe, sha);
    }

    /// <summary>
    /// Downloads the new exe next to the running one, checks it against the published SHA-256 and returns its
    /// path. Nothing is replaced yet; <see cref="LaunchInstaller"/> does that after the app exits.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, Version current, IProgress<double>? progress, CancellationToken ct)
    {
        if (!update.CanInstall) throw new InvalidOperationException("لا يحتوي هذا الإصدار على ملف تثبيت.");
        var target = CurrentExePath ?? throw new InvalidOperationException("تعذّر معرفة مكان البرنامج.");
        var folder = Path.GetDirectoryName(target)!;
        var download = Path.Combine(folder, $"{ExeName}.{update.Version.ToString(3)}.download");

        using var http = CreateHttp(current, TimeSpan.FromMinutes(10));
        var shaText = await http.GetStringAsync(update.ShaUrl, ct);
        var expected = shaText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        if (expected.Length != 64) throw new InvalidDataException("ملف التحقق (sha256) غير صالح.");

        using (var res = await http.GetAsync(update.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(download);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total is > 0) progress?.Report((double)read / total.Value);
            }
        }

        string actual;
        await using (var fs = File.OpenRead(download))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(download);
            throw new InvalidDataException("الملف الذي تم تنزيله لا يطابق البصمة المنشورة؛ لم يُثبَّت شيء.");
        }
        AppLog.Info($"Update {update.Tag} downloaded and verified ({actual[..12]}…)");
        return download;
    }

    /// <summary>
    /// Starts a small hidden script that waits for this process to exit, swaps the exe (keeping the old one as
    /// .old until the new one starts), and launches the new version. The caller then exits the app.
    /// </summary>
    public static void LaunchInstaller(string downloadedExe)
    {
        var target = CurrentExePath ?? throw new InvalidOperationException("تعذّر معرفة مكان البرنامج.");
        var script = Path.Combine(Path.GetTempPath(), $"igsv-update-{Guid.NewGuid():N}.cmd");
        var pid = Environment.ProcessId;
        File.WriteAllText(script, $"""
            @echo off
            setlocal
            :wait
            tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
            if exist "{target}.old" del /f /q "{target}.old"
            move /y "{target}" "{target}.old" >nul || goto fail
            move /y "{downloadedExe}" "{target}" >nul || (move /y "{target}.old" "{target}" >nul & goto fail)
            start "" "{target}"
            del /f /q "{target}.old" 2>nul
            goto end
            :fail
            start "" "{target}"
            :end
            del "%~f0"
            """);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        AppLog.Info("Update installer started; exiting");
    }

    private static string? CurrentExePath => Environment.ProcessPath;
}
