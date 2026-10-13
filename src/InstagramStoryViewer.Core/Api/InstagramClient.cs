using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Api;

/// <summary>
/// Thin client over the endpoints instagram.com's own web app calls, authenticated with the
/// user's browser session cookie. These are undocumented endpoints and may change at any time.
/// </summary>
public sealed class InstagramClient : IDisposable
{
    private const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";

    /// <summary>
    /// Instagram serves the same /api/v1 endpoints from several hosts, and which one answers with JSON
    /// (instead of an HTML page) for a browser session cookie varies over time and by account.
    /// Requests try these in order and stick with the first one that works.
    /// </summary>
    private static readonly EndpointProfile[] Profiles =
    [
        new("web", "https://www.instagram.com", ChromeUserAgent, "936619743392459", BrowserHeaders: true),
        new("web-i", "https://i.instagram.com", ChromeUserAgent, "936619743392459", BrowserHeaders: true),
        // Same headers instaloader uses for its "iPhone" API calls with a web session.
        new("ios", "https://i.instagram.com",
            "Instagram 273.0.0.16.70 (iPad13,8; iOS 16_3; en_US; en-US; scale=2.00; 2048x2732; 452417278) AppleWebKit/420+",
            "124024574287414", BrowserHeaders: false)
    ];

    private sealed record EndpointProfile(string Name, string BaseUrl, string UserAgent, string AppId, bool BrowserHeaders);

    private const int MaxRetries = 3;
    private const int MaxSavedBody = 256 * 1024;
    private static readonly TimeSpan MinDelayBetweenRequests = TimeSpan.FromMilliseconds(1200);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly SessionCookies _cookies;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;
    private int _preferredProfile;
    private int _seenVariant;

    private readonly Action<string>? _log;

    /// <param name="log">Receives status messages such as retry notices (optional).</param>
    public InstagramClient(SessionCookies cookies, Action<string>? log = null)
    {
        _cookies = cookies;
        _log = log;
        var handler = new HttpClientHandler
        {
            UseCookies = false, // cookies are sent manually so we control exactly what goes out
            AllowAutoRedirect = false, // a redirect to /accounts/login means the session is dead
            AutomaticDecompression = DecompressionMethods.All
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Name of the host profile that last returned valid JSON (for diagnostics).</summary>
    public string ActiveProfile => Profiles[_preferredProfile].Name;

    public string? ViewerId => _cookies.DsUserId;

    // ------------------------------------------------------------------ public API

    /// <summary>Resolves a username to its numeric user id.</summary>
    public async Task<ProfileUser> GetProfileAsync(string username, CancellationToken ct = default)
    {
        username = username.Trim().TrimStart('@');
        var res = await SendAsync<WebProfileInfoResponse>(
            HttpMethod.Get, $"/api/v1/users/web_profile_info/?username={Uri.EscapeDataString(username)}", null, ct);

        var user = res.Data?.User;
        if (user?.Id is null)
            throw new InstagramApiException(InstagramErrorKind.NotFound, $"المستخدم @{username} غير موجود.");
        return user;
    }

    /// <summary>The logged-in account (also a cheap way to check that the session works).</summary>
    public async Task<StoryUser> GetCurrentUserAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ViewerId))
            throw new InstagramApiException(InstagramErrorKind.LoginRequired, "تعذّر معرفة معرّف المستخدم من الكوكيز. الصق الكوكيز كاملة.");
        var res = await SendAsync<UserInfoResponse>(HttpMethod.Get, $"/api/v1/users/{ViewerId}/info/", null, ct);
        return res.User ?? throw new InstagramApiException(InstagramErrorKind.InvalidResponse, "لم تُرجع إنستجرام بيانات الحساب.");
    }

    /// <summary>Stories bar of the accounts the logged-in user follows.</summary>
    public async Task<List<Reel>> GetStoriesTrayAsync(CancellationToken ct = default)
    {
        var res = await SendAsync<ReelsTrayResponse>(HttpMethod.Get, "/api/v1/feed/reels_tray/", null, ct);
        return res.Tray?.Where(r => r.User?.Username is not null).ToList() ?? new List<Reel>();
    }

    /// <summary>Current (non-expired) stories of a user. Returns null when the user has none.</summary>
    public async Task<Reel?> GetUserStoriesAsync(string userId, CancellationToken ct = default)
    {
        var res = await SendAsync<ReelsMediaResponse>(
            HttpMethod.Get, $"/api/v1/feed/reels_media/?reel_ids={Uri.EscapeDataString(userId)}", null, ct);

        Reel? reel = null;
        if (res.Reels is not null && res.Reels.TryGetValue(userId, out var r)) reel = r;
        reel ??= res.ReelsMedia?.FirstOrDefault(x => x.Id == userId) ?? res.ReelsMedia?.FirstOrDefault();

        if (reel?.Items is null || reel.Items.Count == 0) return null;
        reel.Items.Sort((a, b) => a.TakenAt.CompareTo(b.TakenAt));
        return reel;
    }

    /// <summary>Marks a single story item as seen (the owner will see you in the viewers list).</summary>
    public async Task MarkAsSeenAsync(StoryItem item, string ownerId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_cookies.CsrfToken))
            throw new InstagramApiException(InstagramErrorKind.Unknown,
                "تعليم الستوري كمشاهَد يحتاج csrftoken. سجّل الدخول عبر زر إنستجرام أو الصق الكوكيز كاملة.");

        var mediaPk = item.Pk ?? item.Id?.Split('_')[0]
            ?? throw new InstagramApiException(InstagramErrorKind.InvalidResponse, "الستوري لا يحتوي على معرّف.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var seenAt = Math.Max(now, item.TakenAt);

        // instagram.com's web app
        var webFields = new Dictionary<string, string>
        {
            ["reelMediaId"] = mediaPk,
            ["reelMediaOwnerId"] = ownerId,
            ["reelId"] = ownerId,
            ["reelMediaTakenAt"] = item.TakenAt.ToString(),
            ["viewSeenAt"] = seenAt.ToString()
        };

        // mobile app: reels = { "<mediaPk>_<ownerPk>_<reelId>": ["<takenAt>_<seenAt>"] }
        var mobilePayload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["reels"] = new Dictionary<string, string[]> { [$"{mediaPk}_{ownerId}_{ownerId}"] = [$"{item.TakenAt}_{seenAt}"] },
            ["container_module"] = "feed_timeline",
            ["reel_media_skipped"] = new Dictionary<string, object>(),
            ["live_vods"] = new Dictionary<string, object>(),
            ["live_vods_skipped"] = new Dictionary<string, object>(),
            ["nuxes"] = new Dictionary<string, object>(),
            ["nuxes_skipped"] = new Dictionary<string, object>(),
            ["_uid"] = ViewerId ?? "",
            ["_uuid"] = Guid.NewGuid().ToString()
        });

        var variants = new (EndpointProfile Profile, string Path, Func<HttpContent> Body)[]
        {
            (Profiles[0], "/stories/reel/seen", () => new FormUrlEncodedContent(webFields)),
            (Profiles[0], "/api/v1/stories/reel/seen", () => new FormUrlEncodedContent(webFields)),
            (Profiles[2], "/api/v2/media/seen/?reel=1&live_vod=0",
                () => new FormUrlEncodedContent(new Dictionary<string, string> { ["signed_body"] = "SIGNATURE." + mobilePayload }))
        };

        // Remember which variant worked so later calls go straight to it.
        var start = _seenVariant;
        var attempts = new List<string>();
        InstagramApiException? first = null;
        for (var i = 0; i < variants.Length; i++)
        {
            var index = (start + i) % variants.Length;
            var v = variants[index];
            try
            {
                await SendWithProfileAsync<ApiResponseBase>(v.Profile, HttpMethod.Post, v.Path, v.Body, ct);
                _seenVariant = index;
                return;
            }
            catch (InstagramApiException ex) when (ex.TryAnotherHost || ex.Kind is InstagramErrorKind.NotFound or InstagramErrorKind.Unknown)
            {
                first ??= ex;
                attempts.Add($"{v.Path}: HTTP {(int?)ex.StatusCode}");
            }
        }

        throw new InstagramApiException(first!.Kind,
            $"تعذّر تعليم الستوري كمشاهَد. {first.Message}\nالمسارات التي جُرّبت: {string.Join("، ", attempts)}",
            first.StatusCode, first)
        {
            ResponseBody = first.ResponseBody,
            ContentType = first.ContentType,
            RequestPath = "stories/seen"
        };
    }

    /// <summary>Downloads raw media bytes from Instagram's CDN.</summary>
    /// <param name="progress">Reports 0..1 when the server sends Content-Length.</param>
    public async Task DownloadAsync(string url, string filePath, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new InstagramApiException(InstagramErrorKind.Unknown,
                $"فشل تنزيل الملف (HTTP {(int)res.StatusCode}).", res.StatusCode);

        var total = res.Content.Headers.ContentLength;
        var tmp = filePath + ".part";
        try
        {
            await using (var src = await res.Content.ReadAsStreamAsync(ct))
            await using (var fs = File.Create(tmp))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total is > 0) progress?.Report((double)read / total.Value);
                }
            }
            File.Move(tmp, filePath, overwrite: true);
            progress?.Report(1);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>Sends a request, falling back to the other host profiles when one answers with HTML.</summary>
    /// <param name="contentFactory">Builds a fresh body per attempt (a sent HttpContent is disposed and cannot be reused).</param>
    private async Task<T> SendAsync<T>(HttpMethod method, string path, Func<HttpContent>? contentFactory, CancellationToken ct)
        where T : ApiResponseBase
    {
        var attempts = new List<string>();
        InstagramApiException? first = null;

        for (var i = 0; i < Profiles.Length; i++)
        {
            var index = (_preferredProfile + i) % Profiles.Length;
            try
            {
                var result = await SendWithProfileAsync<T>(Profiles[index], method, path, contentFactory, ct);
                if (index != _preferredProfile)
                {
                    _log?.Invoke($"تم التبديل إلى مسار الاتصال: {Profiles[index].Name}");
                    _preferredProfile = index;
                }
                return result;
            }
            catch (InstagramApiException ex) when (ex.TryAnotherHost)
            {
                first ??= ex;
                attempts.Add($"{Profiles[index].Name}: HTTP {(int?)ex.StatusCode} {ex.ContentType}".TrimEnd());
            }
        }

        // Every host answered with something other than JSON: report what we got.
        throw new InstagramApiException(first!.Kind,
            $"{first.Message}\nالمسارات التي جُرّبت: {string.Join("، ", attempts)}",
            first.StatusCode, first)
        {
            ResponseBody = first.ResponseBody,
            ContentType = first.ContentType,
            RequestPath = path
        };
    }

    private async Task<T> SendWithProfileAsync<T>(EndpointProfile profile, HttpMethod method, string path,
        Func<HttpContent>? contentFactory, CancellationToken ct) where T : ApiResponseBase
    {
        for (var attempt = 1; ; attempt++)
        {
            await ThrottleAsync(ct);

            using var req = BuildRequest(profile, method, path);
            if (contentFactory is not null)
                req.Content = contentFactory();

            HttpResponseMessage res;
            try
            {
                res = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= MaxRetries)
                    throw new InstagramApiException(InstagramErrorKind.Network, $"تعذّر الاتصال بإنستجرام: {ex.Message}", inner: ex);
                await BackoffAsync(attempt, null, $"خطأ في الشبكة: {ex.Message}", ct);
                continue;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                if (attempt >= MaxRetries)
                    throw new InstagramApiException(InstagramErrorKind.Network, "انتهت مهلة الاتصال بإنستجرام.", inner: ex);
                await BackoffAsync(attempt, null, "انتهت مهلة الطلب", ct);
                continue;
            }

            using (res)
            {
                _cookies.UpdateFrom(res);
                var body = await res.Content.ReadAsStringAsync(ct);

                // Retry-able: rate limits and transient server errors.
                if ((res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500) && attempt < MaxRetries)
                {
                    await BackoffAsync(attempt, res.Headers.RetryAfter, $"HTTP {(int)res.StatusCode}", ct);
                    continue;
                }

                return Interpret<T>(res, body, path);
            }
        }
    }

    private HttpRequestMessage BuildRequest(EndpointProfile profile, HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, profile.BaseUrl + path);
        var h = req.Headers;
        h.TryAddWithoutValidation("User-Agent", profile.UserAgent);
        h.TryAddWithoutValidation("Accept", "*/*");
        h.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        h.TryAddWithoutValidation("X-IG-App-ID", profile.AppId);
        h.TryAddWithoutValidation("Cookie", _cookies.ToHeader());
        if (!string.IsNullOrEmpty(_cookies.CsrfToken))
            h.TryAddWithoutValidation("X-CSRFToken", _cookies.CsrfToken);

        if (profile.BrowserHeaders)
        {
            // What instagram.com's own JavaScript sends; without these some endpoints return the HTML app shell.
            h.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            h.TryAddWithoutValidation("X-ASBD-ID", "129477");
            h.TryAddWithoutValidation("X-IG-WWW-Claim", "0");
            h.TryAddWithoutValidation("Origin", "https://www.instagram.com");
            h.TryAddWithoutValidation("Referer", "https://www.instagram.com/");
            h.TryAddWithoutValidation("Sec-Fetch-Site", profile.BaseUrl.Contains("www.") ? "same-origin" : "same-site");
            h.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            h.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        }
        return req;
    }

    private static T Interpret<T>(HttpResponseMessage res, string body, string path) where T : ApiResponseBase
    {
        var status = res.StatusCode;
        var contentType = res.Content.Headers.ContentType?.MediaType;

        // 3xx to the login page = invalid/expired session.
        if ((int)status is >= 300 and < 400)
        {
            var location = res.Headers.Location?.ToString() ?? "";
            if (location.Contains("challenge", StringComparison.OrdinalIgnoreCase))
                throw new InstagramApiException(InstagramErrorKind.Checkpoint,
                    "إنستجرام يطلب التحقق من الحساب. افتح إنستجرام في المتصفح وأكمل التحقق ثم حاول مجدداً.", status);
            // Another host may still accept the session, so let the caller try it.
            throw new InstagramApiException(InstagramErrorKind.LoginRequired,
                "الجلسة منتهية أو غير صالحة. سجّل الدخول من جديد.", status)
            {
                TryAnotherHost = true, ContentType = contentType, RequestPath = path,
                ResponseBody = $"Redirect → {location}"
            };
        }

        T? parsed = null;
        var json = StripJsonPrefix(body);
        if (json.StartsWith('{'))
        {
            try { parsed = JsonSerializer.Deserialize<T>(json, JsonOptions); }
            catch (JsonException ex)
            {
                throw new InstagramApiException(InstagramErrorKind.InvalidResponse,
                    $"تعذّر تحليل استجابة JSON: {ex.Message}", status, ex)
                {
                    ResponseBody = Truncate(body), ContentType = contentType, RequestPath = path
                };
            }
        }

        var msg = parsed?.Message ?? "";

        if (status == HttpStatusCode.TooManyRequests || msg.Contains("wait a few minutes", StringComparison.OrdinalIgnoreCase)
            || parsed?.Spam == true)
            throw new InstagramApiException(InstagramErrorKind.RateLimited,
                "تم تجاوز حد الطلبات. انتظر عدة دقائق ثم حاول مجدداً.", status);

        if (msg.Contains("checkpoint", StringComparison.OrdinalIgnoreCase) || parsed?.CheckpointUrl is not null
            || msg.Contains("challenge", StringComparison.OrdinalIgnoreCase))
            throw new InstagramApiException(InstagramErrorKind.Checkpoint,
                "إنستجرام يطلب التحقق من الحساب. افتح إنستجرام في المتصفح وأكمل التحقق ثم حاول مجدداً.", status);

        if (status is HttpStatusCode.Unauthorized || parsed?.RequireLogin == true
            || msg.Contains("login_required", StringComparison.OrdinalIgnoreCase))
            throw new InstagramApiException(InstagramErrorKind.LoginRequired,
                "الجلسة منتهية أو غير صالحة. سجّل الدخول من المتصفح وانسخ sessionid جديد.", status);

        if (status == HttpStatusCode.NotFound && parsed is not null)
            throw new InstagramApiException(InstagramErrorKind.NotFound, "العنصر المطلوب غير موجود.", status);

        if (status == HttpStatusCode.Forbidden && parsed is not null)
            throw new InstagramApiException(InstagramErrorKind.PrivateAccount,
                "ليس لديك صلاحية لعرض هذا المحتوى (قد يكون الحساب خاصاً).", status);

        if (parsed is null)
            throw new InstagramApiException(InstagramErrorKind.InvalidResponse,
                $"أرسل إنستجرام صفحة بدلاً من البيانات (HTTP {(int)status}, {contentType ?? "?"}): {Snippet(body)}", status)
            {
                TryAnotherHost = true, ResponseBody = Truncate(body), ContentType = contentType, RequestPath = path
            };

        if (!res.IsSuccessStatusCode)
            throw new InstagramApiException(InstagramErrorKind.Unknown, $"خطأ من إنستجرام (HTTP {(int)status}): {msg}", status);

        if (string.Equals(parsed.Status, "fail", StringComparison.OrdinalIgnoreCase))
            throw new InstagramApiException(InstagramErrorKind.Unknown, $"خطأ من إنستجرام: {msg}", status);

        return parsed;
    }

    /// <summary>Removes anti-JSON-hijacking prefixes such as "for (;;);" that some Meta endpoints add.</summary>
    private static string StripJsonPrefix(string body)
    {
        var t = body.TrimStart();
        foreach (var prefix in new[] { "for (;;);", "while(1);", ")]}'" })
            if (t.StartsWith(prefix, StringComparison.Ordinal))
                return t[prefix.Length..].TrimStart();
        return t;
    }

    private static string Truncate(string body) => body.Length > MaxSavedBody ? body[..MaxSavedBody] : body;

    /// <summary>First readable words of a response (tags stripped) for error messages.</summary>
    private static string Snippet(string body)
    {
        var title = System.Text.RegularExpressions.Regex.Match(body, "<title[^>]*>(.*?)</title>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        var text = title.Success ? "<title> " + title.Groups[1].Value : body;
        text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
        return text.Length > 120 ? text[..120] + "…" : text.Length == 0 ? "(فارغة)" : text;
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var wait = _lastRequest + MinDelayBetweenRequests - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastRequest = DateTime.UtcNow;
        }
        finally { _throttle.Release(); }
    }

    private async Task BackoffAsync(int attempt, RetryConditionHeaderValue? retryAfter, string reason, CancellationToken ct)
    {
        var delay = retryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt) * 2);
        if (delay > TimeSpan.FromMinutes(2)) delay = TimeSpan.FromMinutes(2);
        _log?.Invoke($"{reason} — إعادة المحاولة بعد {delay.TotalSeconds:0} ثانية ({attempt}/{MaxRetries - 1})...");
        await Task.Delay(delay, ct);
    }

    public void Dispose()
    {
        _http.Dispose();
        _throttle.Dispose();
    }
}
