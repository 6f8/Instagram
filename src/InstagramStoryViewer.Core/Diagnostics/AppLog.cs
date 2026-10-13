using System.Globalization;

namespace InstagramStoryViewer.Diagnostics;

/// <summary>
/// Small rolling log: one file per day (yyyy-MM-dd.log), kept for <see cref="RetentionDays"/> days.
/// Every line goes through <see cref="DiagnosticsSanitizer"/>, so tokens never reach the disk.
/// </summary>
public static class AppLog
{
    public const int RetentionDays = 7;
    private static readonly object Gate = new();
    private static string? _folder;
    private static Func<DateTime> _now = () => DateTime.Now;

    public static string? Folder => _folder;

    /// <summary>Enables logging into <paramref name="folder"/> and removes logs older than the retention period.</summary>
    public static void Initialize(string folder, Func<DateTime>? clock = null)
    {
        lock (Gate)
        {
            _folder = folder;
            if (clock is not null) _now = clock;
            try
            {
                Directory.CreateDirectory(folder);
                var cutoff = _now().Date.AddDays(-RetentionDays);
                foreach (var file in Directory.EnumerateFiles(folder, "*.log"))
                {
                    if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < cutoff)
                        File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // logging must never break the app
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (_folder is null) return;
            try
            {
                var now = _now();
                var line = $"{now:yyyy-MM-dd HH:mm:ss} [{level}] {DiagnosticsSanitizer.Sanitize(message).Replace('\n', ' ')}{Environment.NewLine}";
                File.AppendAllText(Path.Combine(_folder, $"{now:yyyy-MM-dd}.log"), line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // logging must never break the app
            }
        }
    }
}
