using InstagramStoryViewer.Gui.Resources;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;
using InstagramStoryViewer.Services;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>Loading and previewing a user's stories.</summary>
public sealed partial class MainViewModel
{
    private static string VideoCacheFolder => Path.Combine(Path.GetTempPath(), "InstagramStoryViewer", "video-cache");

    /// <summary>
    /// Windows' media player often cannot stream Instagram's CDN URLs directly, so videos are fetched to a
    /// temp file first (or the downloaded copy is used). Returns null when the video could not be fetched.
    /// </summary>
    public async Task<string?> PrepareVideoAsync(StoryRowViewModel row, CancellationToken ct)
    {
        if (row.LocalPath is not null && File.Exists(row.LocalPath)) return row.LocalPath;
        if (_client is null || row.MediaUrl is null) return null;

        var name = string.Concat((row.Item.Pk ?? row.Item.Id ?? Guid.NewGuid().ToString()).Where(char.IsLetterOrDigit));
        var path = Path.Combine(VideoCacheFolder, name + ".mp4");
        if (File.Exists(path)) return path;

        IsPreparingPreview = true;
        try
        {
            Directory.CreateDirectory(VideoCacheFolder);
            try
            {
                await _client.DownloadAsync(row.MediaUrl, path, ct);
            }
            catch (InstagramApiException ex) when (ex.Kind == InstagramErrorKind.MediaExpired)
            {
                if (!await RefreshRowAsync(row, ct)) throw;
                await _client.DownloadAsync(row.MediaUrl!, path, ct);
            }
            return path;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is InstagramApiException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            ShowToast(string.Format(CultureInfo.CurrentCulture, Strings.PreviewVideoFailed, ex.Message), isError: true);
            return null;
        }
        finally
        {
            IsPreparingPreview = false;
        }
    }

    private static void CleanVideoCache()
    {
        try
        {
            if (!Directory.Exists(VideoCacheFolder)) return;
            foreach (var f in new DirectoryInfo(VideoCacheFolder).EnumerateFiles())
                if (f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) f.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; stories expire anyway
        }
    }

    // ================================================================== stories

    [RelayCommand]
    private async Task SearchAsync()
    {
        var name = SearchText.Trim().TrimStart('@');
        if (name.Length == 0 || _client is null) return;
        var cts = ResetStoriesCts();
        await RunStoriesLoadAsync(name, async ct =>
        {
            var profile = await _client.GetProfileAsync(name, ct);
            if (profile.IsPrivate && !profile.FollowedByViewer && profile.Id != _client.ViewerId)
            {
                EmptyMessage = string.Format(CultureInfo.CurrentCulture, Strings.PrivateAccountNotFollowed, profile.Username);
                return null;
            }
            return (profile.Username ?? name, profile.Id!);
        }, cts.Token);
    }

    [RelayCommand]
    private async Task OpenUserAsync(TrayUserViewModel? user)
    {
        if (user is null || _client is null) return;
        Page = AppPage.Dashboard;
        var cts = ResetStoriesCts();
        await RunStoriesLoadAsync(user.Username, _ => Task.FromResult<(string, string)?>((user.Username, user.UserId)), cts.Token);
    }

    private CancellationTokenSource ResetStoriesCts()
    {
        // Cancel (not dispose) the previous load: its in-flight request may still hold the token.
        _storiesCts.Cancel();
        _storiesCts = new CancellationTokenSource();
        return _storiesCts;
    }

    private async Task RunStoriesLoadAsync(string displayName, Func<CancellationToken, Task<(string Username, string UserId)?>> resolve,
        CancellationToken ct)
    {
        Stories.Clear();
        SelectedStory = null;
        CurrentUsername = displayName;
        _currentUserId = null;
        ResetHighlights();
        EmptyMessage = null;
        IsLoadingStories = true;
        try
        {
            var target = await resolve(ct);
            if (target is null) return;
            var (username, userId) = target.Value;
            CurrentUsername = username;
            _currentUserId = userId;
            NotifyCurrentAccountFlags();

            var reel = await _client!.GetUserStoriesAsync(userId, ct);
            ct.ThrowIfCancellationRequested();
            if (reel?.Items is null)
            {
                EmptyMessage = string.Format(CultureInfo.CurrentCulture, Strings.NoCurrentStories, username);
                return;
            }

            ShowReelItems(reel.Items, username, userId, highlightId: null);
        }
        catch (OperationCanceledException)
        {
            // a newer load replaced this one
        }
        catch (InstagramApiException ex) when ((int?)ex.StatusCode >= 500)
        {
            // Instagram's own server failed for this account: nothing the user can fix, so no error dialog.
            EmptyMessage = string.Format(CultureInfo.CurrentCulture, Strings.StoriesServerError, CurrentUsername);
            await HandleErrorAsync(ex, silent: true);
        }
        catch (InstagramApiException ex)
        {
            EmptyMessage = ex.Message;
            await HandleErrorAsync(ex, silent: false);
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsLoadingStories = false;
        }
    }

    /// <summary>Fills the table with a reel's items (current stories or one highlight).</summary>
    private void ShowReelItems(IEnumerable<Models.StoryItem> items, string username, string userId, string? highlightId)
    {
        Stories.Clear();
        var i = 0;
        foreach (var item in items)
        {
            var row = new StoryRowViewModel(item, username, userId, ++i, highlightId);
            var path = _downloader!.FindExistingPath(row.FolderName, item);
            if (path is not null)
            {
                row.LocalPath = path;
                row.SizeText = Format.Size(new FileInfo(path).Length);
                row.State = DownloadState.Done;
            }
            Stories.Add(row);
        }
        SelectedStory = Stories.FirstOrDefault();
    }

    /// <summary>Re-fetches a story whose CDN link expired and swaps in the fresh item.</summary>
    private async Task<bool> RefreshRowAsync(StoryRowViewModel row, CancellationToken ct)
    {
        if (_client is null) return false;
        try
        {
            var reel = await _client.GetUserStoriesAsync(row.ReelId, ct);
            var fresh = reel?.Items?.FirstOrDefault(i => i.Pk == row.Item.Pk);
            if (fresh is null) return false;
            row.ReplaceItem(fresh);
            return true;
        }
        catch (InstagramApiException)
        {
            return false;
        }
    }

    /// <summary>Runs fire-and-forget UI work without letting an unexpected exception crash the app.</summary>
}
