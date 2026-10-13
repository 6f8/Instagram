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

    public InstagramApiException(InstagramErrorKind kind, string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }
}
