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
    // Small batches: Instagram answers a large batch of accounts you do not follow with HTTP 500 at times,
    // and a failed small batch loses only a few accounts.
    private const int TopicReelsChunk = 5;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WatchTopicCommand))]
    private string _topicTag = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WatchTopicCommand))]
    private bool _isWatchingTopic;

    [ObservableProperty] private string? _topicStatus;

    private bool CanWatchTopic() => !IsWatchingTopic && IsLoggedIn && PostFinder.NormalizeTag(TopicTag).Length > 0;

    [RelayCommand(CanExecute = nameof(CanWatchTopic))]
    private async Task WatchTopicAsync()
    {
        if (_client is not { } client) return;
        var tag = PostFinder.NormalizeTag(TopicTag);
        if (client.CoolingDownUntil is { } until)
        {
            TopicStatus = $"طلب إنستجرام التمهّل؛ لن يُرسل البرنامج شيئاً قبل {until.ToLocalTime():HH:mm}.";
            return;
        }
        var life = _lifetimeCts.Token; // a sign-out replaces it: taken now so a sign-out stops this run
        IsWatchingTopic = true;
        TopicStatus = $"جاري البحث عن حسابات تنشر في #{tag}...";
        try
        {
            var posts = await _dialogs.WithInstagramPageAsync(client.Cookies.ToHeader(), async (fetch, ct) =>
            {
                var found = ReadTopicPosts(await fetch(PostFinder.HashtagPath(tag), ct), PostFinder.HashtagPath(tag));
                if (found.Count == 0)
                {
                    await Task.Delay(FollowListReader.Pause(), ct);
                    found = ReadTopicPosts(await fetch(PostFinder.SearchPath("#" + tag), ct), PostFinder.SearchPath("#" + tag));
                }
                return found;
            });
            life.ThrowIfCancellationRequested();
            // Never your own account or one you excluded from watching.
            var accounts = posts.Where(p => p.UserPk is not null && p.Username.Length > 0
                                            && p.UserPk != client.ViewerId && !IsExcluded(p.UserPk))
                .DistinctBy(p => p.UserPk).Take(TopicMaxAccounts).ToList();
            if (accounts.Count == 0)
            {
                TopicStatus = $"لم يُعثر على حسابات في #{tag}.";
                return;
            }

            TopicStatus = $"وُجد {accounts.Count} حساباً. جاري جلب ستورياتهم...";
            var failed = 0;
            var reels = await client.GetStoriesForUsersAsync(accounts.Select(a => a.UserPk!), life, TopicReelsChunk,
                (ids, ex) =>
                {
                    failed += ids.Count;
                    AppLog.Warn($"Topic #{tag}: stories of {ids.Count} accounts skipped, HTTP {(int?)ex.StatusCode}");
                });
            if (failed == accounts.Count)
            {
                TopicStatus = $"خادم إنستجرام رفض جلب ستوري حسابات #{tag} (خطأ 500 من عنده). جرّب لاحقاً أو موضوعاً آخر.";
                return;
            }
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
                TopicStatus = $"الحسابات الـ {accounts.Count - failed} في #{tag} ليس لديها ستوري الآن. جرّب موضوعاً آخر أو لاحقاً."
                              + (failed > 0 ? $" (تعذّر جلب {failed} حساباً)" : "");
                return;
            }

            var accountsWithStories = targets.Select(t => t.UserId).Distinct().Count();
            TopicStatus = $"تُعرض {targets.Count} ستوري من {accountsWithStories} حساباً في #{tag}...";
            var ok = await WatchAsync(targets, WatchMode.Manual, allowLike: false);
            TopicStatus = $"شاهدت {ok} من {targets.Count} ستوري من {accountsWithStories} حساباً في #{tag}.";
        }
        catch (InstagramApiException ex) when (ex.Kind == InstagramErrorKind.RateLimited)
        {
            TopicStatus = "طلب إنستجرام التمهّل. انتظر ساعة تقريباً ثم جرّب مرة واحدة.";
            await HandleErrorAsync(ex, silent: true);
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

    /// <summary>
    /// Posts in a page answer; a throttled or signed-out answer is an error instead of "no posts", so it is not
    /// followed by a second request and the user is told why.
    /// </summary>
    private static List<PostSummary> ReadTopicPosts(PageResult res, string path)
    {
        if (res.Status is 429 or 0 || res.Body.Contains("wait a few minutes", StringComparison.OrdinalIgnoreCase))
            throw new InstagramApiException(InstagramErrorKind.RateLimited, "طلب إنستجرام التمهّل.") { RequestPath = path };
        if (res.Status == 401 || res.Body.Contains("login_required", StringComparison.OrdinalIgnoreCase))
            throw new InstagramApiException(InstagramErrorKind.LoginRequired, "المتصفح المدمج غير مسجّل الدخول.") { RequestPath = path };
        return PostFinder.Extract(res.Body);
    }
}
