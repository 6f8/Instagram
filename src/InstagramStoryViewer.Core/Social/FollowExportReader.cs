using System.IO.Compression;
using System.Text.Json;

namespace InstagramStoryViewer.Social;

/// <summary>
/// Reads the followers and following lists from Instagram's own "Download your information" export
/// (JSON format, as a .zip or the extracted folder). The export is complete and exact and needs no requests,
/// so it is the reliable way to answer "who does not follow me back".
/// </summary>
public static class FollowExportReader
{
    public sealed record ExportLists(List<string> Following, HashSet<string> Followers);

    public static ExportLists ReadPath(string path)
    {
        var files = new List<(string Name, string Json)>();
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories))
                if (IsListFile(f)) files.Add((Path.GetFileName(f), File.ReadAllText(f)));
        }
        else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var e in zip.Entries.Where(e => IsListFile(e.FullName)))
            {
                using var r = new StreamReader(e.Open());
                files.Add((e.Name, r.ReadToEnd()));
            }
        }
        else files.Add((Path.GetFileName(path), File.ReadAllText(path)));
        return Read(files);
    }

    private static bool IsListFile(string name)
    {
        var n = Path.GetFileName(name).ToLowerInvariant();
        return n == "following.json" || (n.StartsWith("followers_", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal));
    }

    public static ExportLists Read(IEnumerable<(string Name, string Json)> files)
    {
        var following = new List<string>();
        var followers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawFollowing = false;
        var sawFollowers = false;
        foreach (var (name, json) in files)
        {
            var isFollowing = name.Equals("following.json", StringComparison.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray()
                : root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).SelectMany(p => p.Value.EnumerateArray());
            foreach (var item in items)
            {
                var u = Username(item);
                if (u is null) continue;
                if (isFollowing) { if (!following.Contains(u, StringComparer.OrdinalIgnoreCase)) following.Add(u); }
                else followers.Add(u);
            }
            if (isFollowing) sawFollowing = true; else sawFollowers = true;
        }
        if (!sawFollowing || !sawFollowers)
            throw new InvalidDataException(!sawFollowing ? "following.json" : "followers_1.json");
        return new ExportLists(following, followers);
    }

    /// <summary>Older exports put the name in string_list_data[0].value, newer ones in title or only in the link.</summary>
    private static string? Username(JsonElement item)
    {
        string? href = null;
        if (item.TryGetProperty("string_list_data", out var sl) && sl.ValueKind == JsonValueKind.Array && sl.GetArrayLength() > 0)
        {
            var d = sl[0];
            if (d.TryGetProperty("value", out var v) && v.GetString() is { Length: > 0 } val) return val.Trim();
            if (d.TryGetProperty("href", out var h)) href = h.GetString();
        }
        if (item.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } title) return title.Trim();
        if (href is null) return null;
        var last = href.TrimEnd('/').Split('/').LastOrDefault();
        return string.IsNullOrEmpty(last) || last == "_u" ? null : last;
    }
}
