using System.Text.Json;
using InstagramStoryViewer.Storage;

namespace InstagramStoryViewer.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "igsv-db-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "history.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Appends_and_loads_newest_first_with_all_fields()
    {
        using var store = new HistoryStore(Db);
        var t = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Local);
        store.Append([
            new WatchRecord(t, "a", "1", WatchMode.Auto, WatchOutcome.Verified),
            new WatchRecord(t.AddMinutes(1), "b", "2", WatchMode.Comment, WatchOutcome.Verified,
                "https://www.instagram.com/p/X/", IsPost: true, Text: "تعليق جميل")
        ]);

        var all = store.Load();
        Assert.Equal(["b", "a"], all.Select(r => r.Username));
        Assert.Equal("تعليق جميل", all[0].Text);
        Assert.True(all[0].IsPost);
        Assert.Equal(WatchMode.Comment, all[0].Mode);
        Assert.Equal(t, all[1].Time);
        Assert.Single(store.Load(max: 1));
    }

    [Fact]
    public void Survives_reopening_and_clear()
    {
        using (var store = new HistoryStore(Db))
            store.Append([new WatchRecord(DateTime.Now, "a", "1", WatchMode.Manual, WatchOutcome.Shown)]);
        using (var store = new HistoryStore(Db))
        {
            Assert.Equal(1, store.Count);
            store.Clear();
            Assert.Equal(0, store.Count);
        }
    }

    [Fact]
    public void Handled_media_is_remembered_and_expires()
    {
        using var store = new HistoryStore(Db);
        var now = DateTime.UtcNow;
        store.MarkHandled(["old"], now.AddDays(-40));
        store.MarkHandled(["new", "new", ""], now);
        Assert.True(store.IsHandled("old"));
        Assert.True(store.IsHandled("new"));
        Assert.Equal(2, store.HandledCount);

        store.Prune(historyAge: TimeSpan.FromDays(180), handledAge: TimeSpan.FromDays(30), now);
        Assert.False(store.IsHandled("old"));
        Assert.True(store.IsHandled("new"));
    }

    [Fact]
    public void Old_history_expires()
    {
        using var store = new HistoryStore(Db);
        store.Append([
            new WatchRecord(DateTime.Now.AddDays(-200), "old", "1", WatchMode.Auto, WatchOutcome.Shown),
            new WatchRecord(DateTime.Now, "new", "2", WatchMode.Auto, WatchOutcome.Shown)
        ]);
        store.Prune(TimeSpan.FromDays(180), TimeSpan.FromDays(30));
        Assert.Equal("new", Assert.Single(store.Load()).Username);
    }

    [Fact]
    public void Imports_the_old_json_lines_file_once()
    {
        Directory.CreateDirectory(_dir);
        var jsonl = Path.Combine(_dir, "watch-history.jsonl");
        File.WriteAllLines(jsonl, [
            JsonSerializer.Serialize(new WatchRecord(DateTime.Now, "a", "1", WatchMode.Auto, WatchOutcome.Verified)),
            "{ not json",
            JsonSerializer.Serialize(new WatchRecord(DateTime.Now, "b", "2", WatchMode.AutoLike, WatchOutcome.Failed))
        ]);

        using var store = new HistoryStore(Db);
        Assert.Equal(2, store.ImportLegacy(jsonl, ["9", "8"]));
        Assert.False(File.Exists(jsonl));
        Assert.True(File.Exists(jsonl + ".imported"));
        Assert.True(store.IsHandled("9"));
        Assert.Equal(0, store.ImportLegacy(jsonl)); // nothing left to import
        Assert.Equal(2, store.Count);
    }
}
