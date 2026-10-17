using System.IO;
using System.Reflection;
using System.Text;

namespace Cinemana.Desktop.Services;

/// <summary>
/// Small local log in the data folder (log.txt, rolled to log.old.txt at 1 MB).
/// Never records stream URLs' signed query strings.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? file;
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0";
    public static void Init(string root)
    {
        try { Directory.CreateDirectory(root); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        file = Path.Combine(root, "log.txt");
        Info($"start {Version} on {Environment.OSVersion}, .NET {Environment.Version}");
    }
    public static void Info(string message) => Write("INFO", message);
    public static void Error(string where, Exception e) => Write("ERROR", $"{where}: {e.GetType().Name}: {Scrub(e.Message)}");
    private static string Scrub(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\?[^\s""']+", "?…");
    private static void Write(string level, string message)
    {
        if (file is null) return;
        lock (Gate)
        {
            try
            {
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Move(file, Path.ChangeExtension(file, ".old.txt"), true);
                File.AppendAllText(file, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    /// <summary>Version, system and the last log lines, ready to paste into a bug report.</summary>
    public static string Report()
    {
        var text = new StringBuilder();
        text.AppendLine($"Cinemana Desktop {Version}").AppendLine($"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}");
        lock (Gate)
        {
            try { if (file is not null && File.Exists(file)) foreach (var line in File.ReadLines(file).TakeLast(200)) text.AppendLine(line); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { text.AppendLine("(تعذّر قراءة السجل)"); }
        }
        return text.ToString();
    }
}
