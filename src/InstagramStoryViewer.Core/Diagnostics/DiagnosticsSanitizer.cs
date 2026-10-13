using System.Text.RegularExpressions;

namespace InstagramStoryViewer.Diagnostics;

/// <summary>
/// Masks tokens and account identifiers in server responses before they are written to disk, so the
/// diagnostics file can be shared without exposing the session.
/// </summary>
public static partial class DiagnosticsSanitizer
{
    public const int MaxLength = 64 * 1024;
    private const string Mask = "***";

    // "key": "value" / "key":123 pairs in JSON or inline scripts
    [GeneratedRegex("""(?<key>"(?:csrf_token|csrftoken|sessionid|fb_dtsg|lsd|jazoest|ds_user_id|viewer_id|viewerId|user_id|userID|actorID|device_id|machine_id|ig_did|mid|rur|claim|www_claim|token|access_token|encrypted_password)"\s*:\s*)(?<value>"(?:[^"\\]|\\.)*"|\d+)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecret();

    // cookie / query style name=value
    [GeneratedRegex(@"(?<key>\b(?:sessionid|csrftoken|ds_user_id|mid|ig_did|rur|fb_dtsg|lsd|shbid|shbts)=)(?<value>[^;&\s""']+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PairSecret();

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length > MaxLength) text = text[..MaxLength] + "\n…(truncated)";
        text = JsonSecret().Replace(text, m => m.Groups["key"].Value + "\"" + Mask + "\"");
        text = PairSecret().Replace(text, m => m.Groups["key"].Value + Mask);
        return text;
    }
}
