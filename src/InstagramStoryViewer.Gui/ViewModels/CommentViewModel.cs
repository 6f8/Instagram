using InstagramStoryViewer.Gui.Resources;
using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;
using InstagramStoryViewer.Social;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>
/// Posts one comment, written by the user, on one post they chose: paste the link, type the text, confirm.
/// The comment is typed into the post's own comment box in the embedded browser and checked on the page.
/// </summary>
public sealed partial class CommentViewModel : ObservableObject
{
    private readonly IMainContext _ctx;
    private readonly IDialogService _dialogs;

    public CommentViewModel(IMainContext ctx, IDialogService dialogs, ObservableCollection<WatchRecordViewModel> history)
    {
        _ctx = ctx;
        _dialogs = dialogs;
        var view = new ListCollectionView(history) { Filter = o => o is WatchRecordViewModel { Record.Mode: WatchMode.Comment } };
        Recent = view;
    }

    /// <summary>Comments posted from the app (newest first), from the shared history.</summary>
    public ICollectionView Recent { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkHint), nameof(HasLinkHint))]
    [NotifyCanExecuteChangedFor(nameof(PostCommand))]
    private string _postUrl = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CharCountText), nameof(TextHint), nameof(HasTextHint))]
    [NotifyCanExecuteChangedFor(nameof(PostCommand))]
    private string _text = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PostCommand))]
    private bool _isPosting;

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusIsError;

    public string CharCountText => $"{Text.Trim().Length} / {CommentRules.MaxLength}";

    public string? LinkHint => string.IsNullOrWhiteSpace(PostUrl) || PostLink.TryParse(PostUrl) is not null
        ? null
        : Strings.CommentLinkInvalid;

    public bool HasLinkHint => LinkHint is not null;

    public string? TextHint => string.IsNullOrWhiteSpace(Text) ? null : CommentRules.Validate(Text);
    public bool HasTextHint => TextHint is not null;

    /// <summary>Re-evaluates the Post button (called when the login state changes).</summary>
    public void Refresh()
    {
        PostCommand.NotifyCanExecuteChanged();
        SearchHashtagCommand.NotifyCanExecuteChanged();
        LoadNewPostsCommand.NotifyCanExecuteChanged();
    }

    private bool CanPost() => !IsPosting && _ctx.IsLoggedIn && PostLink.TryParse(PostUrl) is not null && CommentRules.Validate(Text) is null;

    [RelayCommand(CanExecute = nameof(CanPost))]
    private async Task PostAsync()
    {
        if (PostLink.TryParse(PostUrl) is not { } link || _ctx.Client is not { } client) return;
        var text = Text.Trim();
        if (CommentRules.Validate(text) is { } problem)
        {
            SetStatus(problem, error: true);
            return;
        }

        IsPosting = true;
        try
        {
            // Best effort: who owns the post, and whether its comments are off (saves opening the page for nothing).
            var info = await TryGetPostInfoAsync(client, link);
            var owner = info?.User?.Username ?? "";
            if (info?.CommentsDisabled == true)
            {
                SetStatus(Strings.CommentsDisabledByOwner, error: true);
                return;
            }

            var already = Recent.Cast<WatchRecordViewModel>().Any(h =>
                h.Record is { Outcome: WatchOutcome.Verified } r && r.Url == link.Url && r.Text == text);
            if (already && !_dialogs.Confirm(Strings.CommentDuplicateConfirm)) return;

            var target = owner.Length > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.CommentTargetOwnerPost, owner) : Strings.CommentTargetPost;
            if (!_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.CommentConfirm, target, Shorten(text, 300)))) return;

            SetStatus(Strings.CommentPosting, error: false);
            AppLog.Info($"Comment start: {link.Kind}/{link.Shortcode} ({text.Length} chars)");
            var result = await _dialogs.CommentAsync(client.Cookies.ToHeader(), link.Url, text);
            AppLog.Info($"Comment result: {result}");

            var (message, isError, outcome) = result switch
            {
                CommentOutcome.Posted => (Strings.CommentPosted, false, WatchOutcome.Verified),
                CommentOutcome.Unconfirmed => (Strings.CommentUnconfirmed, false, WatchOutcome.Shown),
                CommentOutcome.CommentsOff => (Strings.CommentCommentsOff, true, WatchOutcome.Failed),
                CommentOutcome.Rejected => (Strings.CommentRejected, true, WatchOutcome.Failed),
                CommentOutcome.LoginRequired => (Strings.CommentBrowserLoggedOut, true, WatchOutcome.NotShown),
                _ => (Strings.CommentPostUnavailable, true, WatchOutcome.NotShown)
            };
            SetStatus(message, isError);
            _ctx.AddHistory([new WatchRecord(_ctx.Now.LocalDateTime, owner, link.Shortcode, WatchMode.Comment, outcome, link.Url, IsPost: true, text)]);
            if (outcome == WatchOutcome.Verified) Text = "";
        }
        finally
        {
            IsPosting = false;
        }
    }

    private static async Task<Models.FeedPost?> TryGetPostInfoAsync(InstagramClient client, PostLink link)
    {
        if (link.MediaId is not { } id) return null;
        try
        {
            return await client.GetMediaInfoAsync(id);
        }
        catch (InstagramApiException ex)
        {
            AppLog.Warn($"Post info unavailable ({ex.Kind}): {ex.Message}");
            return null;
        }
    }

    [RelayCommand]
    private void OpenPost()
    {
        if (PostLink.TryParse(PostUrl) is { } link) _dialogs.OpenUrl(link.Url);
    }

    private void SetStatus(string message, bool error)
    {
        Status = message;
        StatusIsError = error;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
