using System.Text.Json.Serialization;
using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Json;

/// <summary>
/// Compile-time generated (de)serializers for the API responses: no reflection at run time, faster
/// start-up of the first request, and a build error instead of a silent runtime failure when a model
/// cannot be serialized.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ApiResponseBase))]
[JsonSerializable(typeof(WebProfileInfoResponse))]
[JsonSerializable(typeof(UserInfoResponse))]
[JsonSerializable(typeof(UserFeedResponse))]
[JsonSerializable(typeof(MediaInfoResponse))]
[JsonSerializable(typeof(HighlightsTrayResponse))]
[JsonSerializable(typeof(ReelsTrayResponse))]
[JsonSerializable(typeof(ReelsMediaResponse))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
