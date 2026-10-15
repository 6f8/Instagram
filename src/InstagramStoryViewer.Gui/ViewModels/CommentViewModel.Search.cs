using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;
using InstagramStoryViewer.Social;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>A post found by a search, shown so the user can pick it to comment on.</summary>
public sealed class PostResultViewModel(PostSummary p)
{
    public PostSummary Post { get; } = p;
    public string Username => Post.Username.Length > 0 ? Post.Username : "—";
    public string? Caption => Post.Caption is { Length: > 140 } c ? c[..140] + "…" : Post.Caption;
    public string? ThumbnailUrl => Post.ThumbnailUrl;
    public string AgoText => Post.TakenAt > 0 ? Format.Ago(Post.TakenAtTime) : "";
    public string Stats => $"♥ {Post.Likes:N0}   💬 {Post.Comments:N0}";
    public bool IsVideo => Post.IsVideo;
}

/// <summary>
/// Finding a post to comment on: a hashtag search (read inside the instagram.com page, like the site's own
/// hashtag page) or the newest posts of the accounts you follow. The user picks one; nothing is posted until
/// they write the comment and confirm.
/// </summary>
public sealed partial class CommentViewModel
{
    private const int NewPostsAccounts = 8;
    private static readonly TimeSpan NewPostsMaxAge = TimeSpan.FromDays(3);

    public ObservableCollection<PostResultViewModel> Results { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchHashtagCommand))]
    private string _hashtag = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchHashtagCommand), nameof(LoadNewPostsCommand))]
    private bool _isSearching;

    [ObservableProperty] private string? _searchStatus;

    private bool CanSearch() => !IsSearching && _ctx.IsLoggedIn && PostFinder.NormalizeTag(Hashtag).Length > 0;
    private bool CanLoadNew() => !IsSearching && _ctx.IsLoggedIn;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchHashtagAsync()
    {
        if (_ctx.Client is not { } client) return;
        var tag = PostFinder.NormalizeTag(Hashtag);
        await RunSearchAsync(async () =>
        {
            // The hashtag page first; the general search results page if it has no posts.
            var posts = await _dialogs.WithInstagramPageAsync(client.Cookies.ToHeader(), async (fetch, ct) =>
            {
                var found = PostFinder.Extract((await fetch(PostFinder.HashtagPath(tag), ct)).Body);
                if (found.Count == 0) found = PostFinder.Extract((await fetch(PostFinder.SearchPath("#" + tag), ct)).Body);
                return found;
            });
            AppLog.Info($"Hashtag search #{tag}: {posts.Count} posts");
            return (posts, posts.Count == 0 ? $"لم يُعثر على منشورات لـ #{tag}." : $"{posts.Count} منشوراً لـ #{tag} (الأحدث أولاً).");
        });
    }

    /// <summary>The last few days' posts of your favorites (or of accounts with new stories when there are none).</summary>
    [RelayCommand(CanExecute = nameof(CanLoadNew))]
    private async Task LoadNewPostsAsync()
    {
        if (_ctx.Client is not { } client) return;
        await RunSearchAsync(async () =>
        {
            var accounts = _ctx.FavoriteRefs.Select(f => (f.UserId, f.Username))
                .Concat(_ctx.TrayAccounts.Select(t => (t.UserId, t.Username)))
                .DistinctBy(a => a.UserId)
                .Take(NewPostsAccounts)
                .ToList();
            if (accounts.Count == 0) return (new List<PostSummary>(), "أضف حسابات إلى المفضّلة لتظهر منشوراتها الجديدة هنا.");
            var posts = new List<PostSummary>();
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < accounts.Count; i++)
            {
                SearchStatus = $"جاري جلب المنشورات الجديدة: {i + 1} من {accounts.Count}...";
                foreach (var p in await client.GetUserPostsAsync(accounts[i].UserId, 4))
                {
                    if (p.Pk is null || p.Code is null || now - DateTimeOffset.FromUnixTimeSeconds(p.TakenAt) > NewPostsMaxAge) continue;
                    posts.Add(new PostSummary(p.Pk, p.Code, p.User?.Username ?? accounts[i].Username, null, null, p.TakenAt, 0, 0, p.MediaType == 2));
                }
            }
            posts = posts.OrderByDescending(p => p.TakenAt).ToList();
            return (posts, posts.Count == 0 ? "لا منشورات جديدة خلال آخر 3 أيام." : $"{posts.Count} منشوراً جديداً من {accounts.Count} حساباً.");
        });
    }

    private async Task RunSearchAsync(Func<Task<(List<PostSummary> Posts, string Message)>> search)
    {
        IsSearching = true;
        SearchStatus = "جاري البحث...";
        try
        {
            var (posts, message) = await search();
            Results.Clear();
            foreach (var p in posts) Results.Add(new PostResultViewModel(p));
            SearchStatus = message;
        }
        catch (InstagramApiException ex)
        {
            SearchStatus = $"تعذّر البحث: {ex.Message}";
            await _ctx.HandleErrorAsync(ex, silent: true);
        }
        catch (OperationCanceledException)
        {
            SearchStatus = "أُوقف البحث.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Error("Post search failed in the embedded browser", ex);
            SearchStatus = $"تعذّر البحث: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Puts the chosen post in the link box, ready for the comment.</summary>
    [RelayCommand]
    private void UsePost(PostResultViewModel? result)
    {
        if (result is null) return;
        PostUrl = result.Post.Url;
        Status = $"اخترت منشور {result.Username}. اكتب تعليقك ثم اضغط نشر.";
        StatusIsError = false;
    }

    [RelayCommand]
    private void OpenResult(PostResultViewModel? result)
    {
        if (result is not null) _dialogs.OpenUrl(result.Post.Url);
    }
}
