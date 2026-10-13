using System.Text.RegularExpressions;

namespace InstagramStoryViewer.Social;

/// <summary>A link to one Instagram post or reel, as pasted by the user.</summary>
public sealed partial record PostLink(string Shortcode, string Kind)
{
    /// <summary>Canonical URL of the post (what the embedded browser opens).</summary>
    public string Url => $"https://www.instagram.com/{Kind}/{Shortcode}/";

    // instagram.com/p/CODE, /reel/CODE, /reels/CODE, /tv/CODE, optionally after a username
    // (instagram.com/name/p/CODE), with or without scheme, www, trailing slash or query string.
    [GeneratedRegex(@"^(?:https?://)?(?:www\.|m\.)?instagram\.com/(?:[A-Za-z0-9._]+/)?(p|reel|reels|tv)/([A-Za-z0-9_-]{5,64})/?(?:[?#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    /// <summary>Parses a pasted link. Returns null when it is not a link to a single post or reel.</summary>
    public static PostLink? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = LinkPattern().Match(text.Trim());
        if (!m.Success) return null;
        var kind = m.Groups[1].Value.ToLowerInvariant() switch
        {
            "reels" => "reel",
            var k => k
        };
        return new PostLink(m.Groups[2].Value, kind);
    }
}

/// <summary>Instagram's own limits for a comment, checked before anything is sent.</summary>
public static partial class CommentRules
{
    public const int MaxLength = 2200;
    public const int MaxHashtags = 30;
    public const int MaxMentions = 5;

    [GeneratedRegex(@"(?<![\w])#\w+")]
    private static partial Regex Hashtag();

    [GeneratedRegex(@"(?<![\w])@[A-Za-z0-9._]+")]
    private static partial Regex Mention();

    /// <summary>Null when the comment can be posted, otherwise the reason (Arabic, shown to the user).</summary>
    public static string? Validate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "اكتب نص التعليق أولاً.";
        var t = text.Trim();
        if (t.Length > MaxLength) return $"التعليق أطول من الحد المسموح ({MaxLength} حرف).";
        if (Hashtag().Count(t) > MaxHashtags) return $"لا يسمح إنستجرام بأكثر من {MaxHashtags} هاشتاق في التعليق.";
        if (Mention().Count(t) > MaxMentions) return $"لا يسمح إنستجرام بأكثر من {MaxMentions} إشارات (@) في التعليق.";
        return null;
    }
}
