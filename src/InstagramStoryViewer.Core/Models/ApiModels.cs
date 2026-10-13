using System.Text.Json.Serialization;
using InstagramStoryViewer.Json;

namespace InstagramStoryViewer.Models;

/// <summary>Fields every Instagram API response may carry (used for error detection).</summary>
public class ApiResponseBase
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("require_login")] public bool? RequireLogin { get; set; }
    [JsonPropertyName("checkpoint_url")] public string? CheckpointUrl { get; set; }
    [JsonPropertyName("spam")] public bool? Spam { get; set; }
}

// ---------- /api/v1/users/web_profile_info/?username=... ----------

public sealed class WebProfileInfoResponse : ApiResponseBase
{
    [JsonPropertyName("data")] public WebProfileData? Data { get; set; }
}

public sealed class WebProfileData
{
    [JsonPropertyName("user")] public ProfileUser? User { get; set; }
}

public sealed class ProfileUser
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; set; }

    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("full_name")] public string? FullName { get; set; }
    [JsonPropertyName("is_private")] public bool IsPrivate { get; set; }
    [JsonPropertyName("followed_by_viewer")] public bool FollowedByViewer { get; set; }
}

// ---------- /api/v1/users/{id}/info/ ----------

public sealed class UserInfoResponse : ApiResponseBase
{
    [JsonPropertyName("user")] public StoryUser? User { get; set; }
}

// ---------- /api/v1/feed/reels_tray/ ----------

public sealed class ReelsTrayResponse : ApiResponseBase
{
    [JsonPropertyName("tray")] public List<Reel>? Tray { get; set; }
}

// ---------- /api/v1/feed/reels_media/?reel_ids=... ----------

public sealed class ReelsMediaResponse : ApiResponseBase
{
    /// <summary>Older shape: object keyed by reel id.</summary>
    [JsonPropertyName("reels")] public Dictionary<string, Reel>? Reels { get; set; }

    /// <summary>Newer shape: plain array.</summary>
    [JsonPropertyName("reels_media")] public List<Reel>? ReelsMedia { get; set; }
}

public sealed class Reel
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; set; }

    [JsonPropertyName("user")] public StoryUser? User { get; set; }
    [JsonPropertyName("items")] public List<StoryItem>? Items { get; set; }
    [JsonPropertyName("media_count")] public int? MediaCount { get; set; }
    [JsonPropertyName("latest_reel_media")] public long? LatestReelMedia { get; set; }
    [JsonPropertyName("expiring_at")] public long? ExpiringAt { get; set; }

    /// <summary>Timestamp of the last item the viewer has seen (0 = nothing seen).</summary>
    [JsonPropertyName("seen")] public long? Seen { get; set; }

    [JsonIgnore]
    public bool HasUnseen => LatestReelMedia is > 0 && (Seen ?? 0) < LatestReelMedia;
}

public sealed class StoryUser
{
    [JsonPropertyName("pk")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Pk { get; set; }

    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("full_name")] public string? FullName { get; set; }
    [JsonPropertyName("is_private")] public bool? IsPrivate { get; set; }
    [JsonPropertyName("profile_pic_url")] public string? ProfilePicUrl { get; set; }
}

public sealed class StoryItem
{
    /// <summary>Usually "{mediaPk}_{ownerPk}".</summary>
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; set; }

    [JsonPropertyName("pk")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Pk { get; set; }

    [JsonPropertyName("taken_at")] public long TakenAt { get; set; }
    [JsonPropertyName("expiring_at")] public long? ExpiringAt { get; set; }

    /// <summary>1 = image, 2 = video, 8 = carousel.</summary>
    [JsonPropertyName("media_type")] public int MediaType { get; set; }

    [JsonPropertyName("image_versions2")] public ImageVersions? ImageVersions2 { get; set; }
    [JsonPropertyName("video_versions")] public List<MediaCandidate>? VideoVersions { get; set; }
    [JsonPropertyName("video_duration")] public double? VideoDuration { get; set; }
    [JsonPropertyName("user")] public StoryUser? User { get; set; }

    [JsonIgnore] public bool IsVideo => MediaType == 2 && VideoVersions is { Count: > 0 };

    /// <summary>Highest-resolution media URL for this story (video if present, otherwise image).</summary>
    [JsonIgnore]
    public string? BestUrl => IsVideo
        ? Best(VideoVersions)?.Url
        : Best(ImageVersions2?.Candidates)?.Url;

    /// <summary>Largest still image (for videos this is the cover frame).</summary>
    [JsonIgnore]
    public string? CoverUrl => Best(ImageVersions2?.Candidates)?.Url;

    /// <summary>Smallest image rendition that is still sharp enough for a list thumbnail.</summary>
    [JsonIgnore]
    public string? ThumbnailUrl =>
        ImageVersions2?.Candidates?.Where(c => !string.IsNullOrEmpty(c.Url))
            .OrderBy(c => c.Width < 150 ? int.MaxValue : c.Width)
            .FirstOrDefault()?.Url;

    /// <summary>"width×height" of the rendition <see cref="BestUrl"/> points at.</summary>
    [JsonIgnore]
    public string? Resolution
    {
        get
        {
            var c = IsVideo ? Best(VideoVersions) : Best(ImageVersions2?.Candidates);
            return c is { Width: > 0, Height: > 0 } ? $"{c.Width}×{c.Height}" : null;
        }
    }

    [JsonIgnore]
    public DateTimeOffset TakenAtTime => DateTimeOffset.FromUnixTimeSeconds(TakenAt);

    /// <summary>Stories live for 24h; fall back to that when the API omits expiring_at.</summary>
    [JsonIgnore]
    public DateTimeOffset ExpiresAtTime => DateTimeOffset.FromUnixTimeSeconds(ExpiringAt ?? TakenAt + 24 * 3600);

    private static MediaCandidate? Best(List<MediaCandidate>? candidates) =>
        candidates?.Where(c => !string.IsNullOrEmpty(c.Url))
                   .OrderByDescending(c => (long)c.Width * c.Height)
                   .FirstOrDefault();
}

public sealed class ImageVersions
{
    [JsonPropertyName("candidates")] public List<MediaCandidate>? Candidates { get; set; }
}

public sealed class MediaCandidate
{
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}
