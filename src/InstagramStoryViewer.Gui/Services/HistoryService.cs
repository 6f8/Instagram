using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Storage;

namespace InstagramStoryViewer.Gui.Services;

/// <summary>
/// The app's activity database (%AppData%\InstagramStoryViewer\history.db). On first use it imports the
/// files older versions wrote, then trims rows that are too old to matter.
/// </summary>
public sealed class HistoryService : IDisposable
{
    public static readonly TimeSpan KeepHistory = TimeSpan.FromDays(180);
    public static readonly TimeSpan KeepHandled = TimeSpan.FromDays(30);

    private readonly HistoryStore? _store;

    public HistoryService(SettingsService settings)
    {
        try
        {
            _store = new HistoryStore(Path.Combine(SettingsStore.AppDataFolder, "history.db"));
            var imported = _store.ImportLegacy(Path.Combine(SettingsStore.AppDataFolder, "watch-history.jsonl"),
                settings.PendingHandledImport);
            if (imported > 0 || settings.PendingHandledImport is { Count: > 0 })
            {
                AppLog.Info($"History: imported {imported} records and {settings.PendingHandledImport?.Count ?? 0} handled ids from the old files");
                settings.PendingHandledImport = null;
                settings.Save();
            }
            _store.Prune(KeepHistory, KeepHandled);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            // History is a convenience; the app keeps working without it.
            AppLog.Error("History database unavailable", ex);
            _store?.Dispose();
            _store = null;
        }
    }

    public bool IsAvailable => _store is not null;

    public void Append(IEnumerable<WatchRecord> records) => Try(() => _store?.Append(records));

    public List<WatchRecord> Load(int max = 1000) => Try(() => _store?.Load(max)) ?? new List<WatchRecord>();

    public void Clear() => Try(() => _store?.Clear());

    public bool IsHandled(string mediaPk) => Try(() => _store?.IsHandled(mediaPk)) ?? false;

    public void MarkHandled(IEnumerable<string> mediaPks) => Try(() => _store?.MarkHandled(mediaPks));

    private static void Try(Action action) => Try<object?>(() => { action(); return null; });

    private static T? Try<T>(Func<T?> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            AppLog.Warn($"History database error: {ex.Message}");
            return default;
        }
    }

    public void Dispose() => _store?.Dispose();
}
