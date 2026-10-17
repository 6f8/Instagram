using System.Net.Http;
using System.Text.Json;

namespace Cinemana.Desktop.Services;

/// <summary>Looks for a newer "cinemana-v*" GitHub release; installs it only when the user asks.</summary>
public static class UpdateCheck
{
    public const string ReleasesPage = "https://github.com/6f8/Instagram/releases";
    public sealed record Update(Version Version, string Url, string Notes, string? ExeUrl = null);

    /// <summary>
    /// Downloads the new exe beside the running one, then a small script waits for the app to close,
    /// swaps the files (keeping the old one as Cinemana.old.exe) and starts the new version.
    /// </summary>
    public static async Task<bool> Install(Update update, IProgress<int>? progress, CancellationToken ct)
    {
        if (update.ExeUrl is null || Environment.ProcessPath is not { } current) return false;
        string folder = System.IO.Path.GetDirectoryName(current)!, next = System.IO.Path.Combine(folder, "Cinemana.new.exe");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Cinemana-Desktop/" + Log.Version);
            using var response = await http.GetAsync(update.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = System.IO.File.Create(next))
            {
                var buffer = new byte[81920]; long done = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0) { await output.WriteAsync(buffer.AsMemory(0, read), ct); done += read; if (total > 0) progress?.Report((int)(done * 100 / total.Value)); }
            }
            if (new System.IO.FileInfo(next).Length < 100_000) throw new System.IO.InvalidDataException("ملف التحديث غير مكتمل.");
            string script = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cinemana-update.cmd");
            System.IO.File.WriteAllText(script, $"""
                @echo off
                :wait
                tasklist /fi "PID eq {Environment.ProcessId}" | find "{Environment.ProcessId}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
                move /y "{current}" "{System.IO.Path.Combine(folder, "Cinemana.old.exe")}" >nul
                move /y "{next}" "{current}" >nul
                start "" "{current}"
                del "%~f0"
                """);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            Log.Info($"update {update.Version} downloaded; restarting");
            return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Log.Error("update-install", e); try { System.IO.File.Delete(next); } catch (System.IO.IOException) { } return false; }
    }
    public static async Task<Update?> Find(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Cinemana-Desktop/" + Log.Version);
            using var doc = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/6f8/Instagram/releases?per_page=20", ct));
            var current = Version.Parse(Log.Version);
            Update? best = null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                string tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                if (!tag.StartsWith("cinemana-v", StringComparison.Ordinal) || release.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                if (!Version.TryParse(tag["cinemana-v".Length..], out var version) || version <= current || best is not null && version <= best.Version) continue;
                string url = release.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
                string notes = release.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                string? exe = null;
                if (release.TryGetProperty("assets", out var assets))
                    foreach (var asset in assets.EnumerateArray())
                        if (asset.TryGetProperty("name", out var n) && n.GetString() == "Cinemana.exe" && asset.TryGetProperty("browser_download_url", out var link)) exe = link.GetString();
                best = new(version, url, notes, exe);
            }
            return best;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or JsonException or FormatException or ArgumentException)
        { Log.Error("update-check", e); return null; }
    }
}
