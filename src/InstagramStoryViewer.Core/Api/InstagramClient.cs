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
    private const string BaseUrl = "https://www.instagram.com";
    private const string WebAppId = "936619743392459";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";

    private const int MaxRetries = 3;
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
        _http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        _http.DefaultRequestHeaders.Add("X-IG-App-ID", WebAppId);
        _http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        _http.DefaultRequestHeaders.Referrer = new Uri(BaseUrl + "/");
    }

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
                "تعليم الستوري كمشاهَد يحتاج csrftoken. الصق الكوكيز كاملة (وليس sessionid فقط).");

        var mediaId = item.Pk ?? item.Id?.Split('_')[0]
            ?? throw new InstagramApiException(InstagramErrorKind.InvalidResponse, "الستوري لا يحتوي على معرّف.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fields = new Dictionary<string, string>
        {
            ["reelMediaId"] = mediaId,
            ["reelMediaOwnerId"] = ownerId,
            ["reelId"] = ownerId,
            ["reelMediaTakenAt"] = item.TakenAt.ToString(),
            ["viewSeenAt"] = Math.Max(now, item.TakenAt).ToString()
        };

        await SendAsync<ApiResponseBase>(HttpMethod.Post, "/api/v1/stories/reel/seen",
            () => new FormUrlEncodedContent(fields), ct);
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

    /// <param name="contentFactory">Builds a fresh body per attempt (a sent HttpContent is disposed and cannot be reused).</param>
    private async Task<T> SendAsync<T>(HttpMethod method, string path, Func<HttpContent>? contentFactory, CancellationToken ct)
        where T : ApiResponseBase
    {
        for (var attempt = 1; ; attempt++)
        {
            await ThrottleAsync(ct);

            using var req = new HttpRequestMessage(method, path);
            req.Headers.Add("Cookie", _cookies.ToHeader());
            if (!string.IsNullOrEmpty(_cookies.CsrfToken))
                req.Headers.Add("X-CSRFToken", _cookies.CsrfToken);
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

                return Interpret<T>(res, body);
            }
        }
    }

    private static T Interpret<T>(HttpResponseMessage res, string body) where T : ApiResponseBase
    {
        var status = res.StatusCode;

        // 3xx to the login page = invalid/expired session.
        if ((int)status is >= 300 and < 400)
        {
            var location = res.Headers.Location?.ToString() ?? "";
            if (location.Contains("challenge", StringComparison.OrdinalIgnoreCase))
                throw new InstagramApiException(InstagramErrorKind.Checkpoint,
                    "إنستجرام يطلب التحقق من الحساب. افتح إنستجرام في المتصفح وأكمل التحقق ثم حاول مجدداً.", status);
            throw new InstagramApiException(InstagramErrorKind.LoginRequired,
                "الجلسة منتهية أو غير صالحة. سجّل الدخول من المتصفح وانسخ sessionid جديد.", status);
        }

        T? parsed = null;
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try { parsed = JsonSerializer.Deserialize<T>(body, JsonOptions); }
            catch (JsonException ex)
            {
                throw new InstagramApiException(InstagramErrorKind.InvalidResponse,
                    $"تعذّر تحليل استجابة JSON: {ex.Message}", status, ex);
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

        if (status == HttpStatusCode.NotFound)
            throw new InstagramApiException(InstagramErrorKind.NotFound, "العنصر المطلوب غير موجود.", status);

        if (status == HttpStatusCode.Forbidden)
            throw new InstagramApiException(InstagramErrorKind.PrivateAccount,
                "ليس لديك صلاحية لعرض هذا المحتوى (قد يكون الحساب خاصاً).", status);

        if (!res.IsSuccessStatusCode || parsed is null)
            throw new InstagramApiException(
                parsed is null ? InstagramErrorKind.InvalidResponse : InstagramErrorKind.Unknown,
                parsed is null
                    ? $"استجابة غير متوقعة من الخادم (HTTP {(int)status}). قد تكون الجلسة منتهية."
                    : $"خطأ من إنستجرام (HTTP {(int)status}): {msg}",
                status);

        if (string.Equals(parsed.Status, "fail", StringComparison.OrdinalIgnoreCase))
            throw new InstagramApiException(InstagramErrorKind.Unknown, $"خطأ من إنستجرام: {msg}", status);

        return parsed;
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
