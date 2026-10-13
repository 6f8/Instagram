using System.Text.Json;

namespace InstagramStoryViewer.Gui.Services;

public enum WatchMode { Manual, Preview, Auto, AutoLike }

/// <summary>Result of one watch attempt, as recorded in the history.</summary>
public enum WatchOutcome
{
    NotShown,   // the story could not be opened (expired, deleted, browser not logged in, stopped)
    Shown,      // shown, but Instagram's response did not say whether it counted
    Verified,   // Instagram's "seen" marker confirms the view
    Failed      // shown, but Instagram still reports it as unseen
}

/// <param name="Url">Link to the item (posts); null means the story URL built from username + media id.</param>
/// <param name="IsPost">A post rather than a story (likes only).</param>
public sealed record WatchRecord(DateTime Time, string Username, string MediaPk, WatchMode Mode, WatchOutcome Outcome,
    string? Url = null, bool IsPost = false);

/// <summary>Append-only history of watched stories in %AppData%\InstagramStoryViewer\watch-history.jsonl.</summary>
public static class WatchHistory
{
    private const int KeepOnDisk = 3000;
    private const int TrimThreshold = 5000;
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(SettingsStore.AppDataFolder, "watch-history.jsonl");

    public static void Append(IEnumerable<WatchRecord> records)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.AppDataFolder);
                File.AppendAllLines(FilePath, records.Select(r => JsonSerializer.Serialize(r)));
                TrimIfNeeded();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // history is a convenience; never fail the watch because of it
            }
        }
    }

    /// <summary>Newest first.</summary>
    public static List<WatchRecord> Load(int max = 1000)
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<WatchRecord>();
                return File.ReadLines(FilePath)
                    .Select(TryParse)
                    .OfType<WatchRecord>()
                    .TakeLast(max)
                    .Reverse()
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new List<WatchRecord>();
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try { File.Delete(FilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static WatchRecord? TryParse(string line)
    {
        try { return JsonSerializer.Deserialize<WatchRecord>(line); }
        catch (JsonException) { return null; } // skip a corrupt line instead of losing the whole history
    }

    private static void TrimIfNeeded()
    {
        var lines = File.ReadAllLines(FilePath);
        if (lines.Length <= TrimThreshold) return;
        File.WriteAllLines(FilePath, lines[^KeepOnDisk..]);
    }
}
