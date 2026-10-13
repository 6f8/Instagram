using System.Net;

namespace InstagramStoryViewer.Api;

public enum InstagramErrorKind
{
    Unknown,
    LoginRequired,      // session expired / invalid cookie
    Checkpoint,         // Instagram wants the account verified in the app/browser
    RateLimited,        // HTTP 429 or "Please wait a few minutes"
    NotFound,           // user does not exist
    PrivateAccount,     // private account the viewer does not follow
    InvalidResponse,    // HTML or malformed JSON instead of the expected payload
    Network             // could not reach Instagram at all
}

public sealed class InstagramApiException : Exception
{
    public InstagramErrorKind Kind { get; }
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Raw response (truncated) when Instagram sent something unexpected; useful for diagnosing.</summary>
    public string? ResponseBody { get; init; }
    public string? ContentType { get; init; }
    public string? RequestPath { get; init; }

    /// <summary>The failure is specific to the host that was called; another host may answer properly.</summary>
    internal bool TryAnotherHost { get; init; }

    /// <summary>Copy of this error with the raw response attached.</summary>
    internal InstagramApiException WithResponse(string body, string? contentType, string path) =>
        new(Kind, Message, StatusCode, InnerException)
        {
            ResponseBody = body,
            ContentType = ContentType ?? contentType,
            RequestPath = RequestPath ?? path,
            TryAnotherHost = TryAnotherHost
        };

    public InstagramApiException(InstagramErrorKind kind, string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }
}
