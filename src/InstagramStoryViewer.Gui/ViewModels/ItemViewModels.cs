using CommunityToolkit.Mvvm.ComponentModel;
using InstagramStoryViewer.Gui.Services;
using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Gui.ViewModels;

public enum AppPage { Dashboard, Following, Downloads, History, Settings }

public enum DownloadState { None, Queued, Downloading, Done, Failed }

/// <summary>An account in the stories tray.</summary>
public sealed partial class TrayUserViewModel : ObservableObject
{
    public TrayUserViewModel(Reel reel)
    {
        Username = reel.User!.Username!;
        UserId = reel.User.Pk ?? reel.Id ?? "";
        FullName = reel.User.FullName;
        ProfilePicUrl = reel.User.ProfilePicUrl;
        StoryCount = reel.MediaCount ?? reel.Items?.Count;
        LatestText = reel.LatestReelMedia is > 0
            ? Format.Ago(DateTimeOffset.FromUnixTimeSeconds(reel.LatestReelMedia.Value))
            : "";
        _hasUnseen = reel.HasUnseen;
        LatestReelMedia = reel.LatestReelMedia ?? 0;
    }

    /// <summary>taken_at of the account's newest story (used to spot new stories of favorites).</summary>
    public long LatestReelMedia { get; }

    public string Username { get; }
    public string UserId { get; }
    public string? FullName { get; }
    public string? ProfilePicUrl { get; }
    public int? StoryCount { get; }
    public string LatestText { get; }
    public string Initial => Username.Length > 0 ? Username[..1].ToUpperInvariant() : "?";

    [ObservableProperty] private bool _hasUnseen;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isExcluded;
}

/// <summary>A single story row in the table.</summary>
public sealed partial class StoryRowViewModel : ObservableObject
{
    /// <param name="highlightId">Set for items of a highlight ("highlight:…"); null for current stories.</param>
    public StoryRowViewModel(StoryItem item, string username, string ownerId, int index, string? highlightId = null)
    {
        Item = item;
        Username = username;
        OwnerId = ownerId;
        Index = index;
        HighlightId = highlightId;
        PostedText = Format.Ago(item.TakenAtTime);
        ExpiresText = IsHighlight ? "—" : Format.Remaining(item.ExpiresAtTime);
        TypeText = item.IsVideo ? $"فيديو {Format.Duration(item.VideoDuration)}".Trim() : "صورة";
    }

    public StoryItem Item { get; private set; }

    /// <summary>Swaps in a freshly fetched copy of the story (new, unexpired media links).</summary>
    public void ReplaceItem(StoryItem fresh)
    {
        Item = fresh;
        OnPropertyChanged(string.Empty); // every derived property may have changed
    }
    public string Username { get; }
    public string OwnerId { get; }
    public int Index { get; }
    public string? HighlightId { get; }
    public bool IsHighlight => HighlightId is not null;

    /// <summary>The reel to re-fetch this item from (the user, or the highlight).</summary>
    public string ReelId => HighlightId ?? OwnerId;

    /// <summary>Download sub-folder: highlights are kept apart from regular stories.</summary>
    public string FolderName => IsHighlight ? $"{Username}_highlights" : Username;

    public bool IsVideo => Item.IsVideo;
    public string TypeText { get; }
    public string PostedText { get; }
    public string ExpiresText { get; }
    public string Resolution => Item.Resolution ?? "—";
    public string? ThumbnailUrl => Item.ThumbnailUrl ?? Item.BestUrl;
    public string? MediaUrl => Item.BestUrl;

    /// <summary>Still image shown in the preview (the cover frame for videos, shown until playback starts).</summary>
    public string? PosterSource => IsVideo ? Item.CoverUrl : PreviewSource;

    /// <summary>Downloaded copy when there is one, so the preview plays from disk.</summary>
    public string? PreviewSource => State == DownloadState.Done && LocalPath is not null ? LocalPath : MediaUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewSource), nameof(PosterSource), nameof(StatusText), nameof(IsDownloading))]
    private DownloadState _state;

    [ObservableProperty] private double _progress;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))] private string? _sizeText;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewSource), nameof(PosterSource))] private string? _localPath;
    /// <summary>Result of the last watch of this story (None = not watched by this app).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkedSeen), nameof(SeenTooltip))]
    private WatchOutcome _seenState = WatchOutcome.NotShown;

    public bool MarkedSeen => SeenState is WatchOutcome.Verified or WatchOutcome.Shown;

    public string SeenTooltip => SeenState switch
    {
        WatchOutcome.Verified => "شوهد (أكّد إنستجرام ذلك)",
        WatchOutcome.Shown => "عُرض، لكن لم يتأكد إنستجرام بعد",
        WatchOutcome.Failed => "عُرض لكن إنستجرام لم يسجّله كمشاهَد",
        _ => "تعليم كمشاهَد"
    };
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))] private string? _error;

    public bool IsDownloading => State == DownloadState.Downloading;

    public string StatusText => State switch
    {
        DownloadState.Done => "تم",
        DownloadState.Queued => "في الانتظار",
        DownloadState.Downloading => "",
        DownloadState.Failed => "فشل",
        _ => "—"
    };
}

/// <summary>A file in the downloads folder.</summary>
public sealed class DownloadedFileViewModel
{
    public DownloadedFileViewModel(FileInfo file, string root)
    {
        FullPath = file.FullName;
        Name = file.Name;
        Folder = Path.GetRelativePath(root, file.DirectoryName ?? root);
        SizeBytes = file.Length;
        SizeText = Format.Size(file.Length);
        Modified = file.LastWriteTime;
        DateText = file.LastWriteTime.ToString("yyyy/MM/dd  HH:mm");
        IsVideo = file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase);
    }

    public string FullPath { get; }
    public string Name { get; }
    public string Folder { get; }
    public long SizeBytes { get; }
    public string SizeText { get; }
    public DateTime Modified { get; }
    public string DateText { get; }
    public bool IsVideo { get; }
    public string? ImagePath => IsVideo ? null : FullPath;
}

/// <summary>A row in the watch history page.</summary>
public sealed class WatchRecordViewModel
{
    public WatchRecordViewModel(WatchRecord r)
    {
        Record = r;
        TimeText = r.Time.ToString("yyyy/MM/dd  HH:mm");
        ModeText = r.Mode switch
        {
            WatchMode.Manual => "يدوي",
            WatchMode.Preview => "عند المعاينة",
            _ => "تلقائي"
        };
        ResultText = r.Outcome switch
        {
            WatchOutcome.Verified => "مؤكَّد",
            WatchOutcome.Shown => "عُرض (غير مؤكَّد)",
            WatchOutcome.Failed => "لم يُسجَّل",
            _ => "لم يُعرض"
        };
    }

    public WatchRecord Record { get; }
    public string Username => Record.Username;
    public WatchOutcome Outcome => Record.Outcome;
    public string TimeText { get; }
    public string ModeText { get; }
    public string ResultText { get; }
    public string StoryUrl => $"https://www.instagram.com/stories/{Uri.EscapeDataString(Record.Username)}/{Record.MediaPk}/";
}
