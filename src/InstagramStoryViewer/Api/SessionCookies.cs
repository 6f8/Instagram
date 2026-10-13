using System.Net;

namespace InstagramStoryViewer.Api;

/// <summary>
/// The browser cookies that authenticate requests. Accepts either a bare <c>sessionid</c> value
/// or a full "Cookie:" header copied from the browser DevTools.
/// </summary>
public sealed class SessionCookies
{
    public string SessionId { get; }
    public string? CsrfToken { get; private set; }
    public string? DsUserId { get; }

    private readonly Dictionary<string, string> _all;

    private SessionCookies(Dictionary<string, string> all)
    {
        _all = all;
        SessionId = all["sessionid"];
        all.TryGetValue("csrftoken", out var csrf);
        CsrfToken = csrf;

        // sessionid looks like "1234567890%3AabcDEF%3A12%3A..." – the first part is the user id.
        if (all.TryGetValue("ds_user_id", out var uid))
            DsUserId = uid;
        else
        {
            var first = WebUtility.UrlDecode(SessionId).Split(':')[0];
            if (first.Length > 0 && first.All(char.IsDigit))
            {
                DsUserId = first;
                _all["ds_user_id"] = first;
            }
        }
    }

    public static SessionCookies Parse(string input)
    {
        input = input.Trim();
        if (input.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            input = input["Cookie:".Length..].Trim();

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (input.Contains('='))
        {
            foreach (var part in input.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                dict[part[..eq].Trim()] = part[(eq + 1)..].Trim().Trim('"');
            }
        }
        else
        {
            dict["sessionid"] = input.Trim('"');
        }

        if (!dict.TryGetValue("sessionid", out var sid) || string.IsNullOrWhiteSpace(sid))
            throw new ArgumentException("لم يتم العثور على قيمة sessionid في الكوكيز المدخلة.");

        return new SessionCookies(dict);
    }

    /// <summary>Picks up a refreshed csrftoken from a response's Set-Cookie headers.</summary>
    public void UpdateFrom(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var v in values)
        {
            var nameValue = v.Split(';', 2)[0];
            var eq = nameValue.IndexOf('=');
            if (eq <= 0) continue;
            var name = nameValue[..eq].Trim();
            var value = nameValue[(eq + 1)..].Trim();
            if (name == "csrftoken" && value.Length > 0)
            {
                CsrfToken = value;
                _all[name] = value;
            }
        }
    }

    public string ToHeader() => string.Join("; ", _all.Select(kv => $"{kv.Key}={kv.Value}"));
}
