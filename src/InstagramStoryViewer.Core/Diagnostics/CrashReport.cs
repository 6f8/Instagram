using System.Globalization;
using System.Runtime.InteropServices;

namespace InstagramStoryViewer.Diagnostics;

/// <summary>
/// Writes a crash report (crash-yyyyMMdd-HHmmss.txt) next to the daily logs when something goes wrong
/// that no code handled. Everything goes through <see cref="DiagnosticsSanitizer"/>, so it is safe to share.
/// </summary>
public static class CrashReport
{
    /// <summary>Writes the report and returns its path, or null when it could not be written.</summary>
    public static string? Write(Exception ex, string source, string? folder = null, DateTime? now = null)
    {
        folder ??= AppLog.Folder;
        if (folder is null) return null;
        var time = now ?? DateTime.Now;
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"crash-{time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt");
            var text =
                $"Time: {time:u}\n" +
                $"Source: {source}\n" +
                $"App: {System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version}\n" +
                $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})\n" +
                $".NET: {RuntimeInformation.FrameworkDescription}\n\n" +
                ex;
            File.WriteAllText(path, DiagnosticsSanitizer.Sanitize(text));
            AppLog.Error($"Crash report written ({source})", ex);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
