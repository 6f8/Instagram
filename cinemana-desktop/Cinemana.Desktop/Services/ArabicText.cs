using System.Text;
using System.Text.RegularExpressions;

namespace Cinemana.Desktop.Services;

/// <summary>Search helpers: Arabic spelling variants and "year / rating" words inside a query.</summary>
public static class ArabicText
{
    private static readonly Regex Diacritics = new("[ً-ٰٟـ]", RegexOptions.Compiled);
    /// <summary>أ إ آ → ا، ة → ه، ى → ي، ؤ → و، ئ → ي، without tashkeel or tatweel, lower-case Latin.</summary>
    public static string Normalize(string text)
    {
        var s = new StringBuilder(Diacritics.Replace(text ?? "", ""));
        s.Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ة', 'ه').Replace('ى', 'ي').Replace('ؤ', 'و').Replace('ئ', 'ي');
        return Regex.Replace(s.ToString().ToLowerInvariant(), @"\s+", " ").Trim();
    }
    public static bool Matches(string text, string query) => Normalize(text).Contains(Normalize(query), StringComparison.Ordinal);
    public sealed record Query(string Text, string? Year, int MinRating);
    /// <summary>"اكشن 2023 7+" → text "اكشن", year 2023, rating ≥ 7.</summary>
    public static Query Parse(string query)
    {
        string? year = null; int rating = 0;
        var words = new List<string>();
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Regex.IsMatch(word, @"^(19|20)\d{2}$")) year = word;
            else if (Regex.Match(word, @"^([1-9])\+$") is { Success: true } m) rating = int.Parse(m.Groups[1].Value);
            else words.Add(word);
        }
        return new(string.Join(' ', words), year, rating);
    }
}
