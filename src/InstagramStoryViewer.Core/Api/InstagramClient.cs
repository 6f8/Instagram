using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using InstagramStoryViewer.Json;
using InstagramStoryViewer.Models;
using Polly;
using Polly.Retry;

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

    /// <summary>
    /// After Instagram itself says "too many requests", every call is refused locally for this long
    /// (a circuit breaker): hammering on during a rate limit is what turns it into an account restriction.
    /// </summary>
    public static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(10);

    // Requests are spaced by a random delay so traffic does not look machine-generated.
    private static readonly TimeSpan MinDelayBetweenRequests = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan MaxDelayBetweenRequests = TimeSpan.FromMilliseconds(2600);

    private readonly HttpClient _http;
    private readonly SessionCookies _cookies;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTime _nextRequestAt = DateTime.MinValue;
    private readonly Queue<DateTime> _requestLog = new();
    private int _preferredProfile;
    private string _wwwClaim = "0";
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    // Lazy: ConcurrentDictionary.GetOrAdd may run its factory twice under contention; Lazy guarantees a
    // single download even then (only the stored Lazy's value is ever started).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task>> _inflightDownloads =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<string>? _log;
    private readonly TimeProvider _time;
    private readonly ResiliencePipeline<HttpResponseMessage> _apiRetry;
    private readonly ResiliencePipeline _downloadRetry;
    private DateTime _coolDownUntil = DateTime.MinValue;

    /// <param name="log">Receives status messages such as retry notices (optional).</param>
    public InstagramClient(SessionCookies cookies, Action<string>? log = null)
        : this(cookies, log, new HttpClientHandler
        {
            UseCookies = false, // cookies are sent manually so we control exactly what goes out
            AllowAutoRedirect = false, // a redirect to /accounts/login means the session is dead
            AutomaticDecompression = DecompressionMethods.All
        }, Task.Delay, TimeProvider.System)
    {
    }

    /// <summary>For tests: inject the HTTP handler, a fake delay and a clock so nothing actually waits.</summary>
    internal InstagramClient(SessionCookies cookies, Action<string>? log, HttpMessageHandler handler,
        Func<TimeSpan, CancellationToken, Task> delay, TimeProvider time)
    {
        _cookies = cookies;
        _log = log;
        _delay = delay;
        _time = time;
        // Downloads stream large videos; the per-call timeout is applied separately for API requests.
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _apiRetry = BuildApiRetry();
        _downloadRetry = BuildDownloadRetry();
    }

    // ------------------------------------------------------------------ retry policies (Polly)

    /// <summary>
    /// API calls: network errors, per-attempt timeouts and 5xx are retried twice with exponential
    /// backoff and jitter (Retry-After wins when Instagram sends it). Anything else is final.
    /// </summary>
    private ResiliencePipeline<HttpResponseMessage> BuildApiRetry() =>
        new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = _time }
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = MaxRetries - 1,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(4),
                MaxDelay = TimeSpan.FromMinutes(2),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<AttemptTimeoutException>()
                    // 429 is not retried: asking again seconds after "too many requests" only makes it worse.
                    // It goes straight to the cool-down instead (see SendWithProfileAsync).
                    .HandleResult(r => (int)r.StatusCode >= 500),
                DelayGenerator = args => new ValueTask<TimeSpan?>(RetryAfter(args.Outcome.Result)),
                OnRetry = args =>
                {
                    var reason = args.Outcome.Exception switch
                    {
                        AttemptTimeoutException => "انتهت مهلة الطلب",
                        { } ex => $"خطأ في الشبكة: {ex.Message}",
                        null => $"HTTP {(int)args.Outcome.Result!.StatusCode}"
                    };
                    LogRetry(reason, args.RetryDelay, args.AttemptNumber + 1);
                    return default;
                }
            })
            .Build();

    /// <summary>Downloads: transient network/IO failures and 5xx from the CDN are retried twice.</summary>
    private ResiliencePipeline BuildDownloadRetry() =>
        new ResiliencePipelineBuilder { TimeProvider = _time }
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = MaxRetries - 1,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(4),
                MaxDelay = TimeSpan.FromMinutes(2),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<IOException>()
                    .Handle<AttemptTimeoutException>()
                    .Handle<InstagramApiException>(e => e.Kind == InstagramErrorKind.Unknown && (int?)e.StatusCode >= 500),
                OnRetry = args =>
                {
                    LogRetry($"إعادة محاولة التنزيل: {args.Outcome.Exception?.Message}", args.RetryDelay, args.AttemptNumber + 1);
                    return default;
                }
            })
            .Build();

    private static TimeSpan? RetryAfter(HttpResponseMessage? res)
    {
        var delta = res?.Headers.RetryAfter?.Delta;
        if (delta is null) return null; // fall back to the exponential backoff
        return delta > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : delta;
    }

    private void LogRetry(string reason, TimeSpan delay, int attempt) =>
        _log?.Invoke($"{reason} — إعادة المحاولة بعد {delay.TotalSeconds:0} ثانية ({attempt}/{MaxRetries - 1})...");

    /// <summary>One attempt ran out of time (as opposed to the caller cancelling).</summary>
    private sealed class AttemptTimeoutException(Exception inner) : Exception("timeout", inner);

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Safety cap on API calls per rolling hour. Bursts beyond this look like automation to Instagram and
    /// risk restrictions on the account, so the client refuses (RateLimited) rather than sending them.
    /// </summary>
    public int MaxRequestsPerHour { get; set; } = 180;

    /// <summary>API calls made in the last hour (for display).</summary>
    public int RequestsLastHour
    {
        get
        {
            lock (_requestLog)
            {
                PruneRequestLog();
                return _requestLog.Count;
            }
        }
    }

    /// <summary>The session cookies; subscribe to <see cref="SessionCookies.Changed"/> to persist rotations.</summary>
    public SessionCookies Cookies => _cookies;

    /// <summary>Name of the host profile that last returned valid JSON (for diagnostics).</summary>
    public string ActiveProfile => Profiles[_preferredProfile].Name;

    public string? ViewerId => _cookies.DsUserId;

    // ------------------------------------------------------------------ public API

    /// <summary>Resolves a username to its numeric user id.</summary>
    public async Task<ProfileUser> GetProfileAsync(string username, CancellationToken ct = default)
    {
        username = username.Trim().TrimStart('@');
        var res = await SendAsync<WebProfileInfoResponse>(
            HttpMethod.Get, $"/api/v1/users/web_profile_info/?username={Uri.EscapeDataString(username)}", null, ct).ConfigureAwait(false);

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
        var res = await SendAsync<UserInfoResponse>(HttpMethod.Get, $"/api/v1/users/{ViewerId}/info/", null, ct).ConfigureAwait(false);
        return res.User ?? throw new InstagramApiException(InstagramErrorKind.InvalidResponse, "لم تُرجع إنستجرام بيانات الحساب.");
    }

    /// <summary>Stories bar of the accounts the logged-in user follows.</summary>
    public async Task<List<Reel>> GetStoriesTrayAsync(CancellationToken ct = default)
    {
        var res = await SendAsync<ReelsTrayResponse>(HttpMethod.Get, "/api/v1/feed/reels_tray/", null, ct).ConfigureAwait(false);
        return res.Tray?.Where(r => r.User?.Username is not null).ToList() ?? new List<Reel>();
    }

    /// <summary>Current (non-expired) stories of a user. Returns null when the user has none.</summary>
    public async Task<Reel?> GetUserStoriesAsync(string userId, CancellationToken ct = default)
    {
        var res = await SendAsync<ReelsMediaResponse>(
            HttpMethod.Get, $"/api/v1/feed/reels_media/?reel_ids={Uri.EscapeDataString(userId)}", null, ct).ConfigureAwait(false);

        Reel? reel = null;
        if (res.Reels is not null && res.Reels.TryGetValue(userId, out var r)) reel = r;
        reel ??= res.ReelsMedia?.FirstOrDefault(x => x.Id == userId) ?? res.ReelsMedia?.FirstOrDefault();

        if (reel?.Items is null || reel.Items.Count == 0) return null;
        reel.Items.Sort((a, b) => a.TakenAt.CompareTo(b.TakenAt));
        return reel;
    }

    /// <summary>The newest posts of a profile (newest first).</summary>
    public async Task<List<FeedPost>> GetUserPostsAsync(string userId, int count = 6, CancellationToken ct = default)
    {
        var res = await SendAsync<UserFeedResponse>(
            HttpMethod.Get, $"/api/v1/feed/user/{Uri.EscapeDataString(userId)}/?count={Math.Clamp(count, 1, 12)}", null, ct).ConfigureAwait(false);
        return res.Items?.Where(p => p.Code is not null).OrderByDescending(p => p.TakenAt).ToList() ?? new List<FeedPost>();
    }

    /// <summary>One post by its numeric media id (owner, like state, whether comments are off).</summary>
    public async Task<FeedPost?> GetMediaInfoAsync(string mediaId, CancellationToken ct = default)
    {
        var res = await SendAsync<MediaInfoResponse>(
            HttpMethod.Get, $"/api/v1/media/{Uri.EscapeDataString(mediaId)}/info/", null, ct).ConfigureAwait(false);
        return res.Items?.FirstOrDefault();
    }

    /// <summary>The highlights (saved story collections) shown on a profile.</summary>
    public async Task<List<Highlight>> GetHighlightsAsync(string userId, CancellationToken ct = default)
    {
        var res = await SendAsync<HighlightsTrayResponse>(
            HttpMethod.Get, $"/api/v1/highlights/{Uri.EscapeDataString(userId)}/highlights_tray/", null, ct).ConfigureAwait(false);
        return res.Tray?.Where(h => h.Id is not null).ToList() ?? new List<Highlight>();
    }

    /// <summary>Current stories of several users in one request (up to 20 ids per call).</summary>
    public async Task<List<Reel>> GetStoriesForUsersAsync(IEnumerable<string> userIds, CancellationToken ct = default)
    {
        var result = new List<Reel>();
        foreach (var chunk in userIds.Distinct().Chunk(20))
        {
            var query = string.Join("&", chunk.Select(id => "reel_ids=" + Uri.EscapeDataString(id)));
            var res = await SendAsync<ReelsMediaResponse>(HttpMethod.Get, "/api/v1/feed/reels_media/?" + query, null, ct).ConfigureAwait(false);
            var reels = res.ReelsMedia ?? res.Reels?.Values.ToList() ?? new List<Reel>();
            foreach (var reel in reels.Where(r => r.Items is { Count: > 0 }))
            {
                reel.Items!.Sort((a, b) => a.TakenAt.CompareTo(b.TakenAt));
                result.Add(reel);
            }
        }
        return result;
    }

    /// <summary>
    /// Downloads raw media bytes from Instagram's CDN. Concurrent calls for the same file share one
    /// download, partial files never replace a complete one, and transient failures are retried.
    /// </summary>
    /// <param name="progress">Reports 0..1 when the server sends Content-Length.</param>
    public Task DownloadAsync(string url, string filePath, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var key = Path.GetFullPath(filePath);
        // The shared download is not tied to the first caller's token: a cancelled preview must not abort a
        // download another caller is waiting for. Each caller can still stop waiting via its own token.
        var lazy = _inflightDownloads.GetOrAdd(key,
            _ => new Lazy<Task>(() => DownloadCoreAsync(url, key, progress, CancellationToken.None)));
        var task = lazy.Value;
        // Remove the entry once finished (successfully or not) so a later retry starts fresh.
        task.ContinueWith(_ => _inflightDownloads.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, lazy)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task.WaitAsync(ct);
    }

    private async Task DownloadCoreAsync(string url, string filePath, IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            await _downloadRetry.ExecuteAsync(async token =>
                await DownloadOnceAsync(url, filePath, progress, token).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        catch (AttemptTimeoutException ex)
        {
            throw new InstagramApiException(InstagramErrorKind.Network, "انتهت مهلة تنزيل الملف.", inner: ex.InnerException);
        }
    }

    private async Task DownloadOnceAsync(string url, string filePath, IProgress<double>? progress, CancellationToken ct)
    {
        // Unique temp name: two writers can never collide on the same ".part" file.
        var tmp = $"{filePath}.{Guid.NewGuid():N}.part";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", ChromeUserAgent);
            using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headersCts.CancelAfter(ApiTimeout);
            HttpResponseMessage res;
            try
            {
                res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headersCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new AttemptTimeoutException(ex);
            }

            using (res)
            {
                if (!res.IsSuccessStatusCode)
                {
                    var expired = res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone or HttpStatusCode.NotFound;
                    throw new InstagramApiException(expired ? InstagramErrorKind.MediaExpired : InstagramErrorKind.Unknown,
                        expired
                            ? "انتهت صلاحية رابط الملف (HTTP " + (int)res.StatusCode + ")."
                            : $"فشل تنزيل الملف (HTTP {(int)res.StatusCode}).", res.StatusCode);
                }

                var total = res.Content.Headers.ContentLength;
                long read = 0;
                var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (src.ConfigureAwait(false))
                {
                    var fs = File.Create(tmp);
                    await using (fs.ConfigureAwait(false))
                    {
                        var buffer = new byte[81920];
                        int n;
                        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            read += n;
                            if (total is > 0) progress?.Report((double)read / total.Value);
                        }
                    }
                }

                if (read == 0 || (total is > 0 && read != total))
                    throw new IOException($"الملف وصل ناقصاً ({read} من {total} بايت).");
            }

            File.Move(tmp, filePath, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            TryDelete(tmp); // no-op once moved into place
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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
                var result = await SendWithProfileAsync<T>(Profiles[index], method, path, contentFactory, ct).ConfigureAwait(false);
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
        Func<HttpContent>? contentFactory, CancellationToken ct, string? referer = null) where T : ApiResponseBase
    {
        HttpResponseMessage res;
        try
        {
            res = await _apiRetry.ExecuteAsync(async token =>
            {
                ThrowIfRevoked();
                await ThrottleAsync(token).ConfigureAwait(false);

                using var req = BuildRequest(profile, method, path, referer);
                if (contentFactory is not null)
                    req.Content = contentFactory();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ApiTimeout);
                HttpResponseMessage r;
                try
                {
                    r = await _http.SendAsync(req, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
                {
                    throw new AttemptTimeoutException(ex);
                }

                // Cookies rotate on every answer, retried ones included.
                _cookies.UpdateFrom(r);
                if (_cookies.IsRevoked) r.Dispose();
                ThrowIfRevoked();
                // instagram.com's JS echoes this value back on later requests; so do we.
                if (r.Headers.TryGetValues("x-ig-set-www-claim", out var claims) && claims.FirstOrDefault() is { Length: > 0 } claim)
                    _wwwClaim = claim;
                return r;
            }, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InstagramApiException(InstagramErrorKind.Network, $"تعذّر الاتصال بإنستجرام: {ex.Message}", inner: ex);
        }
        catch (AttemptTimeoutException ex)
        {
            throw new InstagramApiException(InstagramErrorKind.Network, "انتهت مهلة الاتصال بإنستجرام.", inner: ex.InnerException);
        }

        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                return Interpret<T>(res, body, path);
            }
            catch (InstagramApiException ex) when (ex.Kind == InstagramErrorKind.RateLimited)
            {
                // Instagram (not our own hourly cap) asked us to slow down: stop everything for a while.
                _coolDownUntil = _time.GetUtcNow().UtcDateTime + RateLimitCooldown;
                _log?.Invoke($"طلب إنستجرام التمهّل؛ أوقف البرنامج الطلبات حتى {_coolDownUntil.ToLocalTime():HH:mm} لحماية حسابك.");
                throw ex.ResponseBody is null ? ex.WithResponse(Truncate(body), res.Content.Headers.ContentType?.MediaType, path) : ex;
            }
            catch (InstagramApiException ex) when (ex.ResponseBody is null)
            {
                // Attach the raw response to every API error so failures can be diagnosed.
                throw ex.WithResponse(Truncate(body), res.Content.Headers.ContentType?.MediaType, path);
            }
        }
    }

    /// <summary>Until when Instagram's rate limit keeps the client paused (null when it is not paused).</summary>
    public DateTime? CoolingDownUntil => _coolDownUntil > _time.GetUtcNow().UtcDateTime ? _coolDownUntil : null;

    private HttpRequestMessage BuildRequest(EndpointProfile profile, HttpMethod method, string path, string? referer = null)
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
            h.TryAddWithoutValidation("X-IG-WWW-Claim", _wwwClaim);
            h.TryAddWithoutValidation("Origin", "https://www.instagram.com");
            h.TryAddWithoutValidation("Referer", referer ?? "https://www.instagram.com/");
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
            try { parsed = (T?)JsonSerializer.Deserialize(json, typeof(T), ApiJsonContext.Default); }
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

    private void ThrowIfRevoked()
    {
        if (_cookies.IsRevoked)
            throw new InstagramApiException(InstagramErrorKind.LoginRequired,
                "أنهى إنستجرام هذه الجلسة (حذف ملف تعريف الجلسة). سجّل الدخول من جديد.");
    }

    private void PruneRequestLog()
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        while (_requestLog.Count > 0 && _requestLog.Peek() < cutoff)
            _requestLog.Dequeue();
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (CoolingDownUntil is { } until)
                throw new InstagramApiException(InstagramErrorKind.RateLimited,
                    $"طلب إنستجرام التمهّل، فأوقف البرنامج الطلبات مؤقتاً لحماية حسابك. حاول بعد {until.ToLocalTime():HH:mm}.");

            lock (_requestLog)
            {
                PruneRequestLog();
                if (_requestLog.Count >= MaxRequestsPerHour)
                {
                    var freeAt = _requestLog.Peek().AddHours(1).ToLocalTime();
                    throw new InstagramApiException(InstagramErrorKind.RateLimited,
                        $"وصل البرنامج إلى حد الأمان ({MaxRequestsPerHour} طلب في الساعة) لحماية حسابك. حاول بعد {freeAt:HH:mm}.");
                }
            }

            var wait = _nextRequestAt - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await _delay(wait, ct).ConfigureAwait(false);

            var jitter = Random.Shared.NextDouble() * (MaxDelayBetweenRequests - MinDelayBetweenRequests).TotalMilliseconds;
            _nextRequestAt = DateTime.UtcNow + MinDelayBetweenRequests + TimeSpan.FromMilliseconds(jitter);
            lock (_requestLog) _requestLog.Enqueue(DateTime.UtcNow);
        }
        finally { _throttle.Release(); }
    }

    public void Dispose()
    {
        _http.Dispose();
        _throttle.Dispose();
    }
}
