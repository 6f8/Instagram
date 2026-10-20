using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Social;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>
/// Watching by topic: a hashtag gives the public accounts posting about it, and their current stories play in
/// the watch window like those of followed accounts. Only watching: no like is given, nothing is followed.
/// </summary>
public sealed partial class MainViewModel
{
    private const int TopicMaxAccounts = 20;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WatchTopicCommand))]
    private string _topicTag = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WatchTopicCommand))]
    private bool _isWatchingTopic;

    [ObservableProperty] private string? _topicStatus;

    private bool CanWatchTopic() => !IsWatchingTopic && _client is not null && PostFinder.NormalizeTag(TopicTag).Length > 0;

    [RelayCommand(CanExecute = nameof(CanWatchTopic))]
    private async Task WatchTopicAsync()
    {
        if (_client is not { } client) return;
        var tag = PostFinder.NormalizeTag(TopicTag);
        IsWatchingTopic = true;
        TopicStatus = $"جاري البحث عن حسابات تنشر في #{tag}...";
        try
        {
            var posts = await _dialogs.WithInstagramPageAsync(client.Cookies.ToHeader(), async (fetch, ct) =>
            {
                var found = PostFinder.Extract((await fetch(PostFinder.HashtagPath(tag), ct)).Body);
                if (found.Count == 0) found = PostFinder.Extract((await fetch(PostFinder.SearchPath("#" + tag), ct)).Body);
                return found;
            });
            var accounts = posts.Where(p => p.UserPk is not null && p.Username.Length > 0)
                .DistinctBy(p => p.UserPk).Take(TopicMaxAccounts).ToList();
            if (accounts.Count == 0)
            {
                TopicStatus = $"لم يُعثر على حسابات في #{tag}.";
                return;
            }

            TopicStatus = $"وُجد {accounts.Count} حساباً. جاري جلب ستورياتهم...";
            var reels = await client.GetStoriesForUsersAsync(accounts.Select(a => a.UserPk!), _lifetimeCts.Token);
            var names = accounts.ToDictionary(a => a.UserPk!, a => a.Username);
            var targets = reels
                .Where(r => r.Items is { Count: > 0 })
                .SelectMany(r =>
                {
                    var id = r.User?.Pk ?? r.Id ?? "";
                    var name = r.User?.Username ?? names.GetValueOrDefault(id, "");
                    return r.Items!.Select(i => new WatchTarget(name, id, i, null));
                })
                .Where(t => t.Username.Length > 0)
                .ToList();
            AppLog.Info($"Topic #{tag}: {accounts.Count} accounts, {reels.Count} with stories, {targets.Count} stories");
            if (targets.Count == 0)
            {
                TopicStatus = $"الحسابات الـ {accounts.Count} في #{tag} ليس لديها ستوري الآن. جرّب موضوعاً آخر أو لاحقاً.";
                return;
            }

            var accountsWithStories = targets.Select(t => t.UserId).Distinct().Count();
            TopicStatus = $"تُعرض {targets.Count} ستوري من {accountsWithStories} حساباً في #{tag}...";
            var ok = await WatchAsync(targets, WatchMode.Manual, allowLike: false);
            TopicStatus = $"شاهدت {ok} من {targets.Count} ستوري من {accountsWithStories} حساباً في #{tag}.";
        }
        catch (InstagramApiException ex)
        {
            TopicStatus = $"تعذّر: {ex.Message}";
            await HandleErrorAsync(ex, silent: true);
        }
        catch (OperationCanceledException)
        {
            TopicStatus = "أُوقفت المشاهدة.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Error("Topic watch failed in the embedded browser", ex);
            TopicStatus = $"تعذّر: {ex.Message}";
        }
        finally
        {
            IsWatchingTopic = false;
        }
    }
}
