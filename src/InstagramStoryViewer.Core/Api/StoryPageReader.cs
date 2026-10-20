using System.Text.Json;
using InstagramStoryViewer.Json;
using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Api;

/// <summary>
/// Finds an account's stories in what instagram.com's story page loaded (the JSON embedded in the page and the
/// answers of its own requests). Used when Instagram refuses the stories endpoint for that account but the page,
/// which is what a person sees in the browser, still shows them. The page's data keeps the API's item shape
/// (pk, taken_at, image_versions2, video_versions), wherever it is nested.
/// </summary>
public static class StoryPageReader
{
    private const int MaxDepth = 64;

    /// <summary>The account's reel with the most items, oldest item first; null when none is found.</summary>
    public static Reel? FindReel(IEnumerable<string> texts, string username, string? userId)
    {
        Reel? best = null;
        foreach (var text in texts)
        {
            foreach (var doc in ParseAll(text))
            {
                using (doc)
                {
                    foreach (var reel in Reels(doc.RootElement, 0))
                    {
                        if (!IsOwner(reel, username, userId)) continue;
                        if (best is null || reel.Items!.Count > best.Items!.Count) best = reel;
                    }
                }
            }
        }
        best?.Items!.Sort((a, b) => a.TakenAt.CompareTo(b.TakenAt));
        return best;
    }

    /// <summary>One JSON document, or several (one per line, some with a "for (;;);" guard).</summary>
    private static IEnumerable<JsonDocument> ParseAll(string text)
    {
        if (TryParse(text) is { } whole)
        {
            yield return whole;
            yield break;
        }
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("for (;;);", StringComparison.Ordinal)) t = t[9..];
            if (t.Length > 1 && TryParse(t) is { } part) yield return part;
        }
    }

    private static JsonDocument? TryParse(string text)
    {
        try { return JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 }); }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<Reel> Reels(JsonElement e, int depth)
    {
        if (depth > MaxDepth) yield break;
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray())
                foreach (var r in Reels(x, depth + 1)) yield return r;
            yield break;
        }
        if (e.ValueKind != JsonValueKind.Object) yield break;

        if (e.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            && items.EnumerateArray().Any(IsStoryItem) && TryReadReel(e) is { } reel)
        {
            yield return reel;
            yield break; // the items themselves hold no further reels
        }
        foreach (var p in e.EnumerateObject())
            foreach (var r in Reels(p.Value, depth + 1)) yield return r;
    }

    private static bool IsStoryItem(JsonElement i) =>
        i.ValueKind == JsonValueKind.Object && i.TryGetProperty("taken_at", out _)
        && (i.TryGetProperty("image_versions2", out _) || i.TryGetProperty("video_versions", out _));

    private static Reel? TryReadReel(JsonElement e)
    {
        try
        {
            var reel = e.Deserialize(ApiJsonContext.Default.Reel);
            if (reel?.Items is null) return null;
            reel.Items.RemoveAll(i => i.Pk is null && i.Id is null);
            return reel.Items.Count > 0 ? reel : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsOwner(Reel reel, string username, string? userId)
    {
        var owner = reel.User ?? reel.Items!.Select(i => i.User).FirstOrDefault(u => u is not null);
        if (owner is null) return false;
        return string.Equals(owner.Username, username, StringComparison.OrdinalIgnoreCase)
               || (userId is not null && owner.Pk == userId);
    }
}
