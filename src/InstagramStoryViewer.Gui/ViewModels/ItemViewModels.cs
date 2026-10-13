using CommunityToolkit.Mvvm.ComponentModel;
using InstagramStoryViewer.Gui.Services;
using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Gui.ViewModels;

public enum AppPage { Dashboard, Following, Downloads, Settings }

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
    }

    public string Username { get; }
    public string UserId { get; }
    public string? FullName { get; }
    public string? ProfilePicUrl { get; }
    public int? StoryCount { get; }
    public string LatestText { get; }
    public string Initial => Username.Length > 0 ? Username[..1].ToUpperInvariant() : "?";

    [ObservableProperty] private bool _hasUnseen;
}

/// <summary>A single story row in the table.</summary>
public sealed partial class StoryRowViewModel : ObservableObject
{
    public StoryRowViewModel(StoryItem item, string username, string ownerId, int index)
    {
        Item = item;
        Username = username;
        OwnerId = ownerId;
        Index = index;
        PostedText = Format.Ago(item.TakenAtTime);
        ExpiresText = Format.Remaining(item.ExpiresAtTime);
        TypeText = item.IsVideo ? $"فيديو {Format.Duration(item.VideoDuration)}".Trim() : "صورة";
    }

    public StoryItem Item { get; }
    public string Username { get; }
    public string OwnerId { get; }
    public int Index { get; }

    public bool IsVideo => Item.IsVideo;
    public string TypeText { get; }
    public string PostedText { get; }
    public string ExpiresText { get; }
    public string Resolution => Item.Resolution ?? "—";
    public string? ThumbnailUrl => Item.ThumbnailUrl ?? Item.BestUrl;
    public string? MediaUrl => Item.BestUrl;

    /// <summary>Downloaded copy when there is one, so the preview plays from disk.</summary>
    public string? PreviewSource => State == DownloadState.Done && LocalPath is not null ? LocalPath : MediaUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewSource), nameof(StatusText), nameof(IsDownloading))]
    private DownloadState _state;

    [ObservableProperty] private double _progress;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))] private string? _sizeText;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewSource))] private string? _localPath;
    [ObservableProperty] private bool _markedSeen;
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
