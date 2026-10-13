using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace InstagramStoryViewer.Storage;

/// <summary>
/// Activity history (watched, liked, commented) and the set of media the automation already handled,
/// in one SQLite file. Unlike the old JSON-lines file and the list inside settings.json, both stay fast as
/// they grow, and old rows expire on their own (<see cref="Prune"/>).
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public HistoryStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS history (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                time     TEXT    NOT NULL,
                username TEXT    NOT NULL,
                media_pk TEXT    NOT NULL,
                mode     INTEGER NOT NULL,
                outcome  INTEGER NOT NULL,
                url      TEXT,
                is_post  INTEGER NOT NULL DEFAULT 0,
                text     TEXT
            );
            CREATE INDEX IF NOT EXISTS history_time ON history(time);
            CREATE TABLE IF NOT EXISTS handled (
                media_pk TEXT PRIMARY KEY,
                at       TEXT NOT NULL
            ) WITHOUT ROWID;
            """);
    }

    // ------------------------------------------------------------------ history

    public void Append(IEnumerable<WatchRecord> records)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO history (time, username, media_pk, mode, outcome, url, is_post, text)
                VALUES ($time, $user, $pk, $mode, $outcome, $url, $post, $text)
                """;
            var p = new[] { "$time", "$user", "$pk", "$mode", "$outcome", "$url", "$post", "$text" }
                .Select(n => cmd.Parameters.Add(n, SqliteType.Text)).ToArray();
            foreach (var r in records)
            {
                p[0].Value = r.Time.ToString("o", CultureInfo.InvariantCulture);
                p[1].Value = r.Username;
                p[2].Value = r.MediaPk;
                p[3].Value = (int)r.Mode;
                p[4].Value = (int)r.Outcome;
                p[5].Value = (object?)r.Url ?? DBNull.Value;
                p[6].Value = r.IsPost ? 1 : 0;
                p[7].Value = (object?)r.Text ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Newest first.</summary>
    public List<WatchRecord> Load(int max = 1000)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT time, username, media_pk, mode, outcome, url, is_post, text FROM history ORDER BY id DESC LIMIT $max";
            cmd.Parameters.AddWithValue("$max", max);
            using var reader = cmd.ExecuteReader();
            var list = new List<WatchRecord>();
            while (reader.Read())
            {
                list.Add(new WatchRecord(
                    DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetString(1), reader.GetString(2), (WatchMode)reader.GetInt32(3), (WatchOutcome)reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6) != 0,
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
            return list;
        }
    }

    public int Count => (int)(long)Scalar("SELECT COUNT(*) FROM history")!;

    public void Clear() => Execute("DELETE FROM history");

    // ------------------------------------------------------------------ handled media

    public bool IsHandled(string mediaPk)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM handled WHERE media_pk = $pk";
            cmd.Parameters.AddWithValue("$pk", mediaPk);
            return cmd.ExecuteScalar() is not null;
        }
    }

    public void MarkHandled(IEnumerable<string> mediaPks, DateTime? at = null)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO handled (media_pk, at) VALUES ($pk, $at)";
            var pk = cmd.Parameters.Add("$pk", SqliteType.Text);
            cmd.Parameters.AddWithValue("$at", (at ?? DateTime.UtcNow).ToString("o", CultureInfo.InvariantCulture));
            foreach (var m in mediaPks.Where(m => m.Length > 0))
            {
                pk.Value = m;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public int HandledCount => (int)(long)Scalar("SELECT COUNT(*) FROM handled")!;

    // ------------------------------------------------------------------ upkeep

    /// <summary>Drops history older than <paramref name="historyAge"/> and handled ids older than <paramref name="handledAge"/>.</summary>
    public void Prune(TimeSpan historyAge, TimeSpan handledAge, DateTime? now = null)
    {
        var n = now ?? DateTime.UtcNow;
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM history WHERE time < $h; DELETE FROM handled WHERE at < $m;";
            // History times are local (as shown to the user); handled times are UTC.
            cmd.Parameters.AddWithValue("$h", (n.ToLocalTime() - historyAge).ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$m", (n.ToUniversalTime() - handledAge).ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// One-time import of the old watch-history.jsonl (and the handled list that used to live in settings.json).
    /// The JSON file is renamed afterwards so the import never runs twice. Returns the number of rows imported.
    /// </summary>
    public int ImportLegacy(string jsonlPath, IEnumerable<string>? handled = null)
    {
        var imported = 0;
        if (File.Exists(jsonlPath))
        {
            var records = File.ReadLines(jsonlPath).Select(TryParseLegacy).OfType<WatchRecord>().ToList();
            Append(records);
            imported = records.Count;
            File.Move(jsonlPath, jsonlPath + ".imported", overwrite: true);
        }
        if (handled is not null) MarkHandled(handled);
        return imported;
    }

    private static WatchRecord? TryParseLegacy(string line)
    {
        try { return JsonSerializer.Deserialize<WatchRecord>(line); }
        catch (JsonException) { return null; } // skip a corrupt line instead of losing the whole history
    }

    private void Execute(string sql)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private object? Scalar(string sql)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }
    }

    public void Dispose() => _db.Dispose();
}
