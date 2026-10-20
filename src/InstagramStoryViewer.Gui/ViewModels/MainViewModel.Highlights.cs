using InstagramStoryViewer.Gui.Resources;
using System.Globalization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Models;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>A profile highlight (saved story collection) shown as a chip above the table.</summary>
public sealed partial class HighlightViewModel : ObservableObject
{
    public HighlightViewModel(Highlight h)
    {
        Id = h.Id!;
        Title = string.IsNullOrWhiteSpace(h.Title) ? Strings.HighlightUntitled : h.Title!;
        CoverUrl = h.CoverMedia?.CroppedImageVersion?.Url;
        Count = h.MediaCount;
    }

    public string Id { get; }
    public string Title { get; }
    public string? CoverUrl { get; }
    public int? Count { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>Highlights of the account shown on the dashboard (loaded on demand, one request).</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<HighlightViewModel> Highlights { get; } = new();

    [ObservableProperty] private bool _showHighlights;
    [ObservableProperty] private bool _isLoadingHighlights;

    /// <summary>True while the table lists a highlight rather than the current stories.</summary>
    [ObservableProperty] private bool _isViewingHighlight;

    private void ResetHighlights()
    {
        Highlights.Clear();
        ShowHighlights = false;
        IsViewingHighlight = false;
    }

    [RelayCommand]
    private async Task ToggleHighlightsAsync()
    {
        if (ShowHighlights)
        {
            ShowHighlights = false;
            return;
        }
        if (_client is null || _currentUserId is null) return;
        ShowHighlights = true;
        if (Highlights.Count > 0) return;

        IsLoadingHighlights = true;
        try
        {
            var list = await _client.GetHighlightsAsync(_currentUserId, _storiesCts.Token);
            foreach (var h in list) Highlights.Add(new HighlightViewModel(h));
            if (Highlights.Count == 0) ShowToast(string.Format(CultureInfo.CurrentCulture, Strings.NoHighlights, CurrentUsername));
        }
        catch (InstagramApiException ex)
        {
            ShowHighlights = false;
            await HandleErrorAsync(ex, silent: false);
        }
        catch (OperationCanceledException)
        {
            // another account was opened
        }
        finally
        {
            IsLoadingHighlights = false;
        }
    }

    [RelayCommand]
    private async Task OpenHighlightAsync(HighlightViewModel? highlight)
    {
        if (highlight is null || _client is null || _currentUserId is null || CurrentUsername is null) return;
        foreach (var h in Highlights) h.IsSelected = h == highlight;
        var (username, userId) = (CurrentUsername, _currentUserId);

        Stories.Clear();
        EmptyMessage = null;
        IsLoadingStories = true;
        try
        {
            // Supersedes a stories load still running (a slow story-page read would otherwise overwrite this).
            var reel = await _client.GetUserStoriesAsync(highlight.Id, ResetStoriesCts().Token);
            if (reel?.Items is null)
            {
                EmptyMessage = string.Format(CultureInfo.CurrentCulture, Strings.HighlightEmpty, highlight.Title);
                return;
            }
            ShowReelItems(reel.Items, username, userId, highlight.Id);
            IsViewingHighlight = true;
        }
        catch (InstagramApiException ex)
        {
            EmptyMessage = ex.Message;
            await HandleErrorAsync(ex, silent: false);
        }
        catch (OperationCanceledException)
        {
            // another account was opened
        }
        finally
        {
            IsLoadingStories = false;
        }
    }

    /// <summary>Back from a highlight to the account's current stories.</summary>
    [RelayCommand]
    private async Task ShowCurrentStoriesAsync()
    {
        if (_currentUserId is null || CurrentUsername is null || _client is null) return;
        foreach (var h in Highlights) h.IsSelected = false;
        IsViewingHighlight = false;
        Stories.Clear();
        EmptyMessage = null;
        IsLoadingStories = true;
        try
        {
            var (username, userId) = (CurrentUsername, _currentUserId);
            var (reel, refused) = await LoadCurrentReelAsync(username, userId, ResetStoriesCts().Token);
            if (refused) EmptyMessage = string.Format(CultureInfo.CurrentCulture, Strings.StoriesServerError, username);
            else if (reel?.Items is null) EmptyMessage = string.Format(CultureInfo.CurrentCulture, Strings.NoCurrentStories, CurrentUsername);
            else ShowReelItems(reel.Items, CurrentUsername, _currentUserId, highlightId: null);
        }
        catch (InstagramApiException ex)
        {
            EmptyMessage = ex.Message;
            await HandleErrorAsync(ex, silent: false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsLoadingStories = false;
        }
    }
}
