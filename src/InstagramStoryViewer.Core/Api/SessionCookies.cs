using System.Globalization;
using System.Net;

namespace InstagramStoryViewer.Api;

/// <summary>
/// The browser cookies that authenticate requests. Accepts either a bare <c>sessionid</c> value
/// or a full "Cookie:" header copied from the browser DevTools.
/// </summary>
/// <remarks>
/// Instagram rotates several cookies (sessionid, csrftoken, rur, ...) while the session is used.
/// <see cref="UpdateFrom"/> applies every Set-Cookie it sees so the saved session stays current, and
/// notices when Instagram deletes <c>sessionid</c>, which means the server has logged the session out.
/// </remarks>
public sealed class SessionCookies
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _all;

    public string SessionId { get { lock (_gate) return _all["sessionid"]; } }
    public string? CsrfToken { get { lock (_gate) return _all.GetValueOrDefault("csrftoken"); } }
    public string? DsUserId { get; }

    /// <summary>Instagram told us to delete the session cookie: every further request would fail.</summary>
    public bool IsRevoked { get; private set; }

    /// <summary>Raised (on the calling thread) after Instagram changed one of the cookies.</summary>
    public event EventHandler? Changed;

    private SessionCookies(Dictionary<string, string> all)
    {
        _all = all;

        // sessionid looks like "1234567890%3AabcDEF%3A12%3A..." – the first part is the user id.
        if (all.TryGetValue("ds_user_id", out var uid))
            DsUserId = uid;
        else
        {
            var first = WebUtility.UrlDecode(all["sessionid"]).Split(':')[0];
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

    /// <summary>Applies the Set-Cookie headers of a response.</summary>
    public void UpdateFrom(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            Apply(values);
    }

    /// <summary>Applies raw Set-Cookie header values (separate from <see cref="UpdateFrom"/> for testing).</summary>
    public void Apply(IEnumerable<string> setCookieHeaders)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var header in setCookieHeaders)
            {
                var parts = header.Split(';');
                var eq = parts[0].IndexOf('=');
                if (eq <= 0) continue;
                var name = parts[0][..eq].Trim();
                var value = parts[0][(eq + 1)..].Trim().Trim('"');

                if (value.Length == 0 || IsExpired(parts.Skip(1)))
                {
                    if (name == "sessionid")
                        IsRevoked = true; // keep the old value so callers can still show it; requests are blocked
                    else if (_all.Remove(name))
                        changed = true;
                    continue;
                }

                if (!_all.TryGetValue(name, out var old) || old != value)
                {
                    _all[name] = value;
                    changed = true;
                }
            }
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsExpired(IEnumerable<string> attributes)
    {
        foreach (var attr in attributes)
        {
            var eq = attr.IndexOf('=');
            if (eq <= 0) continue;
            var key = attr[..eq].Trim();
            var val = attr[(eq + 1)..].Trim();
            if (key.Equals("Max-Age", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxAge) && maxAge <= 0)
                return true;
            if (key.Equals("expires", StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.TryParse(val.Replace('-', ' '), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var expires) && expires < DateTimeOffset.UtcNow)
                return true;
        }
        return false;
    }

    public string ToHeader()
    {
        lock (_gate)
            return string.Join("; ", _all.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
