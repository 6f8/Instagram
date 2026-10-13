using InstagramStoryViewer.Gui.Resources;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Automation;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>
/// Auto-like: after each refresh, likes new stories of the chosen accounts and recent posts of favorites,
/// by pressing the real like button in the embedded browser. Bounded by a per-round and a daily limit,
/// quiet hours, the exclusion list and the client's hourly request cap. Runs as a job in the shared queue.
/// </summary>
public sealed partial class AutoLikeViewModel : ObservableObject
{
    public const string JobName = "auto-like";
    public const int LimitMin = 1, LimitMax = 999_999;
    private const int PerRound = 10;
    private const int PostAccountsPerRound = 3; // posts need one request per account: rotate through favorites
    private const int PostsPerAccount = 4;
    private static readonly TimeSpan PostMaxAge = TimeSpan.FromDays(3);

    private readonly IMainContext _ctx;
    private readonly IDialogService _dialogs;
    private readonly SettingsService _settings;
    private readonly HistoryService _history;
    private readonly JobQueue _jobs;

    public AutoLikeViewModel(IMainContext ctx, IDialogService dialogs, SettingsService settings, HistoryService history, JobQueue jobs)
    {
        _ctx = ctx;
        _dialogs = dialogs;
        _settings = settings;
        _history = history;
        _jobs = jobs;
        var s = settings.Current;
#pragma warning disable MVVMTK0034 // load saved values without running the change handlers
        _enabled = s.AutoLikeEnabled;
        _stories = s.AutoLikeStories;
        _posts = s.AutoLikePosts;
        _scope = s.AutoLikeScope;
        _dailyLimit = Math.Clamp(s.AutoLikeDailyLimit, LimitMin, LimitMax);
#pragma warning restore MVVMTK0034
    }

    private AppSettings S => _settings.Current;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _stories;
    [ObservableProperty] private bool _posts;
    [ObservableProperty] private string _scope;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Status))] private int _dailyLimit;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Status))] private bool _isRunning;

    public string Status => string.Format(CultureInfo.CurrentCulture, Strings.AutoLikeTodayStatus, S.AutoLikeCount.Today(_ctx.Now), DailyLimit) + (IsRunning ? Strings.RunningNowSuffix : "");

    partial void OnEnabledChanged(bool value)
    {
        S.AutoLikeEnabled = value;
        _settings.Save();
        _ctx.ScheduleTrayRefresh();
        Queue();
    }

    partial void OnStoriesChanged(bool value)
    {
        S.AutoLikeStories = value;
        _settings.Save();
    }

    partial void OnPostsChanged(bool value)
    {
        S.AutoLikePosts = value;
        _settings.Save();
    }

    partial void OnScopeChanged(string value)
    {
        S.AutoLikeScope = value;
        _settings.Save();
    }

    partial void OnDailyLimitChanged(int value)
    {
        var clamped = Math.Clamp(value, LimitMin, LimitMax);
        if (clamped != value) { DailyLimit = clamped; return; }
        S.AutoLikeDailyLimit = value;
        _settings.Save();
    }

    /// <summary>Queues a round (no-op when off, or when a round is already queued or running).</summary>
    public void Queue()
    {
        if (Enabled && _ctx.IsLoggedIn) _jobs.Enqueue(JobName, RunAsync);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var client = _ctx.Client;
        if (!Enabled || IsRunning || client is null || _ctx.IsQuietNow) return;
        var budget = Math.Min(PerRound, S.AutoLikeCount.Remaining(DailyLimit, _ctx.Now));
        if (budget <= 0) return;

        IsRunning = true;
        try
        {
            var plan = new List<PlannedItem>();
            if (Stories) plan.AddRange(await PlanStoriesAsync(client, budget, ct));
            if (Posts && plan.Count < budget) plan.AddRange(await PlanPostsAsync(client, ct));

            plan = plan.Take(budget).ToList();
            if (plan.Count == 0) return;
            AppLog.Info($"Auto-like start: {plan.Count(p => !p.IsPost)} stories, {plan.Count(p => p.IsPost)} posts");

            var results = await _dialogs.LikeAsync(client.Cookies.ToHeader(),
                plan.Select(p => new LikeRequest(p.Url, IsStory: !p.IsPost)).ToList());
            Record(plan, results);
        }
        catch (InstagramApiException ex)
        {
            await _ctx.HandleErrorAsync(ex, silent: true);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>New (unseen) stories of the accounts in scope that are not liked yet.</summary>
    private async Task<List<PlannedItem>> PlanStoriesAsync(InstagramClient client, int budget, CancellationToken ct)
    {
        var accounts = _ctx.TrayAccounts
            .Where(u => u.HasUnseen && !u.IsExcluded && (Scope == "All" || u.IsFavorite))
            .Take(20)
            .Select(u => new AccountTarget(u.UserId, u.Username))
            .ToList();
        if (accounts.Count == 0) return new List<PlannedItem>();
        var reels = await client.GetStoriesForUsersAsync(accounts.Select(a => a.UserId), ct);
        return Planner.StoriesToLike(reels, accounts, _history.IsHandled, budget);
    }

    /// <summary>Recent, not-yet-liked posts of a few favorites (rotating through the list each round).</summary>
    private async Task<List<PlannedItem>> PlanPostsAsync(InstagramClient client, CancellationToken ct)
    {
        var favorites = _ctx.FavoriteRefs
            .Where(f => !_ctx.IsExcluded(f.UserId))
            .Select(f => new AccountTarget(f.UserId, f.Username))
            .ToList();
        var (batch, next) = Planner.RoundRobin(favorites, S.AutoLikePostCursor, PostAccountsPerRound);
        var plan = new List<PlannedItem>();
        foreach (var account in batch)
        {
            var posts = await client.GetUserPostsAsync(account.UserId, PostsPerAccount, ct);
            plan.AddRange(Planner.PostsToLike(account, posts, _history.IsHandled, PostMaxAge, _ctx.Now));
        }
        S.AutoLikePostCursor = next;
        return plan;
    }

    private void Record(IReadOnlyList<PlannedItem> plan, IReadOnlyList<LikeOutcome> results)
    {
        var records = new List<WatchRecord>();
        var handled = new List<string>();
        var liked = 0;
        for (var i = 0; i < plan.Count; i++)
        {
            var p = plan[i];
            var outcome = i < results.Count ? results[i] : LikeOutcome.NotShown;
            if (outcome == LikeOutcome.Liked) liked++;

            // Anything that reached the page is settled: liked, already liked, or a failure not worth retrying.
            if (outcome != LikeOutcome.NotShown) handled.Add(p.MediaPk);

            // Liking a story plays it, so it is viewed too.
            if (!p.IsPost && outcome is LikeOutcome.Liked or LikeOutcome.AlreadyLiked) _ctx.MarkStoryShown(p.MediaPk);

            var mapped = outcome switch
            {
                LikeOutcome.Liked or LikeOutcome.AlreadyLiked => WatchOutcome.Verified,
                LikeOutcome.NotShown => WatchOutcome.NotShown,
                _ => WatchOutcome.Failed
            };
            records.Add(new WatchRecord(_ctx.Now.LocalDateTime, p.Account.Username, p.MediaPk, WatchMode.AutoLike, mapped,
                p.IsPost ? p.Url : null, p.IsPost));
        }

        _history.MarkHandled(handled);
        S.AutoLikeCount.Add(liked, _ctx.Now);
        _settings.Save();
        _ctx.AddHistory(records);
        OnPropertyChanged(nameof(Status));

        AppLog.Info($"Auto-like done: {liked} liked; outcomes " +
                    string.Join(", ", results.GroupBy(r => r).Select(g => $"{g.Key}={g.Count()}")));
        if (liked > 0) _dialogs.Notify(Strings.AutoLikeTitle, string.Format(CultureInfo.CurrentCulture, Strings.AutoLikeDoneMessage, liked));
    }
}
