using CommunityToolkit.Mvvm.ComponentModel;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>
/// Auto-like: after each refresh, likes new stories of the chosen accounts and recent posts of favorites,
/// by pressing the real like button in the embedded browser. Bounded by a per-cycle and a daily limit,
/// quiet hours, the exclusion list and the client's hourly request cap.
/// </summary>
public sealed partial class MainViewModel
{
    private const int AutoLikePerCycle = 10;
    private const int PostAccountsPerCycle = 3;   // posts need one request per account: rotate through favorites
    private const int PostsPerAccount = 4;
    private const int HandledMemory = 3000;
    private static readonly TimeSpan PostMaxAge = TimeSpan.FromDays(3);

    [ObservableProperty] private bool _autoLikeEnabled;
    [ObservableProperty] private bool _autoLikeStories;
    [ObservableProperty] private bool _autoLikePosts;
    [ObservableProperty] private string _autoLikeScope = "Favorites";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AutoLikeStatus))] private int _autoLikeDailyLimit;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AutoLikeStatus))] private bool _isAutoLiking;

    public string AutoLikeStatus =>
        $"إعجابات اليوم: {AutoLikedToday} من {AutoLikeDailyLimit}" + (IsAutoLiking ? " · جارٍ الآن..." : "");

    private int AutoLikedToday
    {
        get
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (_settings.AutoLikeDay != today)
            {
                _settings.AutoLikeDay = today;
                _settings.AutoLikedToday = 0;
            }
            return _settings.AutoLikedToday;
        }
    }

    private void InitializeAutoLike()
    {
#pragma warning disable MVVMTK0034 // load saved values without running the change handlers
        _autoLikeEnabled = _settings.AutoLikeEnabled;
        _autoLikeStories = _settings.AutoLikeStories;
        _autoLikePosts = _settings.AutoLikePosts;
        _autoLikeScope = _settings.AutoLikeScope;
        _autoLikeDailyLimit = Math.Clamp(_settings.AutoLikeDailyLimit, AutoLikeLimitMin, AutoLikeLimitMax);
#pragma warning restore MVVMTK0034
    }

    partial void OnAutoLikeEnabledChanged(bool value)
    {
        _settings.AutoLikeEnabled = value;
        SaveSettings();
        ScheduleTrayRefresh();
        if (value) _ = SafeAsync(() => RunAutoLikeAsync());
    }

    partial void OnAutoLikeStoriesChanged(bool value)
    {
        _settings.AutoLikeStories = value;
        SaveSettings();
    }

    partial void OnAutoLikePostsChanged(bool value)
    {
        _settings.AutoLikePosts = value;
        SaveSettings();
    }

    partial void OnAutoLikeScopeChanged(string value)
    {
        _settings.AutoLikeScope = value;
        SaveSettings();
    }

    public const int AutoLikeLimitMin = 1, AutoLikeLimitMax = 500;

    partial void OnAutoLikeDailyLimitChanged(int value)
    {
        var clamped = Math.Clamp(value, AutoLikeLimitMin, AutoLikeLimitMax);
        if (clamped != value) { AutoLikeDailyLimit = clamped; return; }
        _settings.AutoLikeDailyLimit = value;
        SaveSettings();
    }

    private sealed record LikeTarget(LikeRequest Request, string Username, string UserId, string MediaPk, bool IsPost);

    private async Task RunAutoLikeAsync()
    {
        if (!AutoLikeEnabled || IsAutoLiking || _client is null || IsQuietNow) return;
        var budget = Math.Min(AutoLikePerCycle, AutoLikeDailyLimit - AutoLikedToday);
        if (budget <= 0) return;

        IsAutoLiking = true;
        try
        {
            var handled = new HashSet<string>(_settings.AutoLikeHandled);
            var queue = new List<LikeTarget>();
            var ct = _lifetimeCts.Token;

            if (AutoLikeStories) await CollectStoryLikesAsync(queue, handled, ct);
            if (AutoLikePosts && queue.Count < budget) await CollectPostLikesAsync(queue, handled, ct);

            queue = queue.Take(budget).ToList();
            if (queue.Count == 0) return;
            AppLog.Info($"Auto-like start: {queue.Count(q => !q.IsPost)} stories, {queue.Count(q => q.IsPost)} posts");

            var results = await _dialogs.LikeAsync(_client.Cookies.ToHeader(), queue.Select(q => q.Request).ToList());
            RecordLikeResults(queue, results);
        }
        catch (InstagramApiException ex)
        {
            await HandleErrorAsync(ex, silent: true);
        }
        catch (OperationCanceledException)
        {
            // signed out
        }
        finally
        {
            IsAutoLiking = false;
        }
    }

    /// <summary>New (unseen) stories of the accounts in scope that are not liked yet.</summary>
    private async Task CollectStoryLikesAsync(List<LikeTarget> queue, HashSet<string> handled, CancellationToken ct)
    {
        var candidates = TrayUsers
            .Where(u => u.HasUnseen && !u.IsExcluded && (AutoLikeScope == "All" || u.IsFavorite))
            .Take(20)
            .ToList();
        if (candidates.Count == 0) return;

        var reels = await _client!.GetStoriesForUsersAsync(candidates.Select(c => c.UserId), ct);
        foreach (var reel in reels)
        {
            var user = candidates.FirstOrDefault(c => c.UserId == (reel.User?.Pk ?? reel.Id));
            if (user is null) continue;
            var seen = reel.Seen ?? 0;
            foreach (var item in reel.Items!.Where(i => i.TakenAt > seen && i.HasLiked != true))
            {
                var pk = item.Pk ?? item.Id ?? "";
                if (pk.Length == 0 || handled.Contains(pk)) continue;
                queue.Add(new LikeTarget(new LikeRequest(StoryUrl(user.Username, item), IsStory: true),
                    user.Username, user.UserId, pk, IsPost: false));
            }
        }
    }

    /// <summary>Recent, not-yet-liked posts of a few favorites (rotating through the list each cycle).</summary>
    private async Task CollectPostLikesAsync(List<LikeTarget> queue, HashSet<string> handled, CancellationToken ct)
    {
        var favorites = _settings.Favorites.Where(f => !IsExcluded(f.UserId)).ToList();
        if (favorites.Count == 0) return;

        var take = Math.Min(PostAccountsPerCycle, favorites.Count);
        var start = _settings.AutoLikePostCursor % favorites.Count;
        for (var k = 0; k < take; k++)
        {
            var fav = favorites[(start + k) % favorites.Count];
            var posts = await _client!.GetUserPostsAsync(fav.UserId, PostsPerAccount, ct);
            foreach (var post in posts)
            {
                if (post.HasLiked == true || post.Pk is null || post.Url is null || handled.Contains(post.Pk)) continue;
                if (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(post.TakenAt) > PostMaxAge) continue;
                queue.Add(new LikeTarget(new LikeRequest(post.Url, IsStory: false), fav.Username, fav.UserId, post.Pk, IsPost: true));
            }
        }
        _settings.AutoLikePostCursor = (start + take) % favorites.Count;
    }

    private void RecordLikeResults(IReadOnlyList<LikeTarget> queue, IReadOnlyList<LikeOutcome> results)
    {
        var records = new List<WatchRecord>();
        var liked = 0;
        for (var i = 0; i < queue.Count; i++)
        {
            var t = queue[i];
            var outcome = i < results.Count ? results[i] : LikeOutcome.NotShown;
            if (outcome == LikeOutcome.Liked) liked++;

            // Anything that reached the page is settled: liked, already liked, or a failure not worth retrying.
            if (outcome != LikeOutcome.NotShown) _settings.AutoLikeHandled.Add(t.MediaPk);

            // Liking a story plays it, so it is viewed too.
            if (!t.IsPost && outcome is LikeOutcome.Liked or LikeOutcome.AlreadyLiked)
                foreach (var row in Stories.Where(r => r.Item.Pk == t.MediaPk)) row.SeenState = WatchOutcome.Shown;

            var mapped = outcome switch
            {
                LikeOutcome.Liked or LikeOutcome.AlreadyLiked => WatchOutcome.Verified,
                LikeOutcome.NotShown => WatchOutcome.NotShown,
                _ => WatchOutcome.Failed
            };
            records.Add(new WatchRecord(DateTime.Now, t.Username, t.MediaPk, WatchMode.AutoLike, mapped,
                t.IsPost ? t.Request.Url : null, t.IsPost));
        }

        if (_settings.AutoLikeHandled.Count > HandledMemory)
            _settings.AutoLikeHandled.RemoveRange(0, _settings.AutoLikeHandled.Count - HandledMemory);
        _ = AutoLikedToday; // roll the day over before adding
        _settings.AutoLikedToday += liked;
        SaveSettings();

        WatchHistory.Append(records);
        foreach (var r in records) History.Insert(0, new WatchRecordViewModel(r));
        while (History.Count > 1000) History.RemoveAt(History.Count - 1);
        OnPropertyChanged(nameof(AutoLikeStatus));

        AppLog.Info($"Auto-like done: {liked} liked; outcomes " +
                    string.Join(", ", results.GroupBy(r => r).Select(g => $"{g.Key}={g.Count()}")));
        if (liked > 0) _dialogs.Notify("إعجاب تلقائي", $"تم الإعجاب بـ {liked} من الستوريات والمنشورات الجديدة");
    }
}
