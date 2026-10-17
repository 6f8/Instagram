using System.IO;
using System.IO.Compression;

namespace Cinemana.Desktop.Services;

/// <summary>
/// Backup of the personal data (favourites, history, lists, notes, follows, settings, queue) as one zip,
/// and a simple folder sync between computers (for example a OneDrive folder): the newer copy of each file wins.
/// Videos and the image cache are never included.
/// </summary>
public static class Backup
{
    private static readonly string[] Personal = ["favorites", "history", "lists", "notes", "followed", "settings", "downloads", "intros", "subtitle-offsets", "hidden", "saved-searches", "series-folders", "download-history"];
    private static readonly string[] Synced = ["favorites", "history", "lists", "notes", "followed", "intros", "subtitle-offsets", "hidden", "saved-searches"];

    public static void Create(string root, string zipFile)
    {
        if (File.Exists(zipFile)) File.Delete(zipFile);
        using var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create);
        foreach (var name in Personal)
        {
            string file = Path.Combine(root, name + ".json");
            if (File.Exists(file)) zip.CreateEntryFromFile(file, name + ".json");
        }
        Log.Info("backup created");
    }
    /// <summary>Restores the known files from a backup; the current ones are kept as *.before-restore.json.</summary>
    public static int Restore(string root, string zipFile)
    {
        int restored = 0;
        using var zip = ZipFile.OpenRead(zipFile);
        foreach (var entry in zip.Entries)
        {
            string name = Path.GetFileNameWithoutExtension(entry.Name);
            if (!Personal.Contains(name) || entry.FullName != entry.Name) continue; // only top-level known files
            string target = Path.Combine(root, name + ".json");
            if (File.Exists(target)) File.Copy(target, Path.Combine(root, name + ".before-restore.json"), true);
            entry.ExtractToFile(target, true); restored++;
        }
        Log.Info($"backup restored ({restored} files)");
        return restored;
    }
    /// <summary>Two-way sync with a folder: each file is copied in whichever direction is newer.</summary>
    public static (int Up, int Down) Sync(string root, string folder)
    {
        int up = 0, down = 0;
        Directory.CreateDirectory(folder);
        foreach (var name in Synced)
        {
            string local = Path.Combine(root, name + ".json"), remote = Path.Combine(folder, name + ".json");
            bool hasLocal = File.Exists(local), hasRemote = File.Exists(remote);
            if (hasLocal && (!hasRemote || File.GetLastWriteTimeUtc(local) > File.GetLastWriteTimeUtc(remote).AddSeconds(2))) { File.Copy(local, remote, true); up++; }
            else if (hasRemote && (!hasLocal || File.GetLastWriteTimeUtc(remote) > File.GetLastWriteTimeUtc(local).AddSeconds(2))) { File.Copy(remote, local, true); down++; }
        }
        if (up + down > 0) Log.Info($"sync: {up} up, {down} down");
        return (up, down);
    }
}
