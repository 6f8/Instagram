using InstagramStoryViewer.Gui.Resources;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Automation;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;
using InstagramStoryViewer.Services;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>Marking stories as seen (manual and automatic).</summary>
public sealed partial class MainViewModel
{
    // ================================================================== seen

    // Instagram retired the REST "seen" endpoints (they return 404), and the web app now uses internal
    // GraphQL calls whose ids change all the time. A story is therefore marked as seen the way a person
    // does it: by actually showing it in the embedded browser.

    private sealed record WatchTarget(string Username, string UserId, Models.StoryItem Item, StoryRowViewModel? Row);

    [RelayCommand]
    private async Task MarkSeenAsync(StoryRowViewModel? row)
    {
        row ??= SelectedStory;
        if (row is null || _client is null) return;
        if (row.IsHighlight)
        {
            ShowToast(Strings.HighlightCannotBeSeen);
            return;
        }
        if (IsExcluded(row.OwnerId) &&
            !_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.ExcludedWatchConfirm, row.Username)))
            return;
        if (ConfirmBeforeMarkSeen && !_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.WatchOneConfirm, row.Username)))
            return;
        var ok = await WatchAsync([ToTarget(row)], WatchMode.Manual);
        ShowToast(ok > 0 ? Strings.WatchedOne : Strings.NotWatchedOne, isError: ok == 0);
        UpdateTraySeen(row.Username);
    }

    [RelayCommand(CanExecute = nameof(HasCurrentUser))]
    private async Task MarkAllSeenAsync()
    {
        if (_client is null || Stories.Count == 0) return;
        var pending = Stories.Where(s => !s.MarkedSeen && !s.IsHighlight).ToList();
        if (pending.Count == 0)
        {
            ShowToast(Strings.AllAlreadySeen);
            return;
        }
        if (IsExcluded(pending[0].OwnerId) &&
            !_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.ExcludedContinueConfirm, CurrentUsername)))
            return;
        if (ConfirmBeforeMarkSeen && !_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.WatchAllConfirm, pending.Count, CurrentUsername)))
            return;
        var ok = await WatchAsync(pending.Select(ToTarget).ToList(), WatchMode.Manual);
        ShowToast(string.Format(CultureInfo.CurrentCulture, Strings.WatchedXOfY, ok, pending.Count), isError: ok < pending.Count);
        if (CurrentUsername is not null) UpdateTraySeen(CurrentUsername);
    }

    private static WatchTarget ToTarget(StoryRowViewModel row) => new(row.Username, row.OwnerId, row.Item, row);

    /// <summary>
    /// Shows the stories in the embedded browser, then asks Instagram whether they now count as seen.
    /// Every attempt is written to the watch history. Returns how many were shown and not contradicted
    /// by Instagram (verified, or unknown when Instagram gave no "seen" marker).
    /// </summary>
    private async Task<int> WatchAsync(IReadOnlyList<WatchTarget> targets, WatchMode mode, Action<int>? onShown = null, bool allowLike = true)
    {
        if (_client is null || targets.Count == 0) return 0;
        var urls = targets.Select(t => Planner.StoryUrl(t.Username, t.Item)).ToList();
        AppLog.Info($"Watch start: {targets.Count} stories, mode {mode}");

        // "Like every story I watch": the like is pressed in the same visit, within the daily like limit.
        var likes = new Dictionary<int, LikeOutcome>();
        // Never on accounts you do not follow (a searched account, a topic): only followed accounts are in the tray.
        var followed = TrayUsers.Select(u => u.UserId).ToHashSet();
        allowLike &= targets.All(t => followed.Contains(t.UserId));
        var likeBudget = allowLike && LikeWhileWatching ? _settings.AutoLikeCount.Remaining(AutoLike.DailyLimit, Now) : 0;
        var shown = await _dialogs.WatchStoriesAsync(_client.Cookies.ToHeader(), urls, background: mode != WatchMode.Manual, onShown,
            like: likeBudget > 0, onLiked: (i, r) => likes[i] = r);
        var verified = await VerifySeenAsync(targets.Where((_, i) => i < shown.Count && shown[i]).ToList());

        var records = new List<WatchRecord>();
        var ok = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            var key = t.Item.Pk ?? t.Item.Id ?? "";
            var outcome = !(i < shown.Count && shown[i])
                ? WatchOutcome.NotShown
                : verified.GetValueOrDefault(key) switch
                {
                    true => WatchOutcome.Verified,
                    false => WatchOutcome.Failed,
                    null => WatchOutcome.Shown
                };
            if (outcome is WatchOutcome.Verified or WatchOutcome.Shown) ok++;
            if (t.Row is not null && outcome != WatchOutcome.NotShown) t.Row.SeenState = outcome;
            records.Add(new WatchRecord(Now.LocalDateTime, t.Username, key, mode, outcome));
        }

        RecordWatchLikes(targets, likes);
        AddHistory(records);

        AppLog.Info($"Watch done: {ok}/{targets.Count} ok; outcomes " +
                    string.Join(", ", records.GroupBy(r => r.Outcome).Select(g => $"{g.Key}={g.Count()}")));
        return ok;
    }

    /// <summary>Likes given while watching: counted in the daily like limit, remembered, and written to the history.</summary>
    private void RecordWatchLikes(IReadOnlyList<WatchTarget> targets, Dictionary<int, LikeOutcome> likes)
    {
        if (likes.Count == 0) return;
        var records = new List<WatchRecord>();
        foreach (var (i, outcome) in likes)
        {
            var t = targets[i];
            var key = t.Item.Pk ?? t.Item.Id ?? "";
            records.Add(new WatchRecord(Now.LocalDateTime, t.Username, key, WatchMode.AutoLike,
                outcome is LikeOutcome.Liked or LikeOutcome.AlreadyLiked ? WatchOutcome.Verified : WatchOutcome.Failed));
        }
        var liked = likes.Count(l => l.Value == LikeOutcome.Liked);
        _settings.AutoLikeCount.Add(liked, Now);
        _history.MarkHandled(likes.Keys.Select(i => targets[i].Item.Pk ?? "").Where(pk => pk.Length > 0));
        SaveSettings();
        AddHistory(records);
        AppLog.Info($"Liked while watching: {liked} of {likes.Count}");
    }

    /// <summary>
    /// Re-reads the reels of the watched accounts: a story counts as seen once the reel's "seen" marker
    /// (taken_at of the last seen item) reaches it. Null = Instagram did not report a marker.
    /// </summary>
    private async Task<Dictionary<string, bool?>> VerifySeenAsync(IReadOnlyList<WatchTarget> shown)
    {
        var result = new Dictionary<string, bool?>();
        if (shown.Count == 0 || _client is null) return result;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), _lifetimeCts.Token); // give Instagram a moment to record it
            var reels = await _client.GetStoriesForUsersAsync(shown.Select(t => t.UserId), _lifetimeCts.Token);
            var seenByUser = reels.ToDictionary(r => r.User?.Pk ?? r.Id ?? "", r => r.Seen);
            foreach (var t in shown)
            {
                var key = t.Item.Pk ?? t.Item.Id ?? "";
                result[key] = seenByUser.TryGetValue(t.UserId, out var seen) && seen is > 0
                    ? seen >= t.Item.TakenAt
                    : null;
            }
        }
        catch (Exception ex) when (ex is InstagramApiException or OperationCanceledException)
        {
            AppLog.Warn($"Seen verification skipped: {ex.Message}");
        }
        return result;
    }

    // ================================================================== auto-watch rules

    public bool IsExcluded(string userId) => _settings.ExcludedFromWatch.Any(a => a.UserId == userId);

    /// <summary>True inside the configured quiet hours (wrapping past midnight, e.g. 1 → 8 or 23 → 7).</summary>
    public bool IsQuietNow => QuietHours.Contains(QuietHoursEnabled, QuietFrom, QuietTo, Now.Hour);

    private int AutoWatchBudget => _settings.AutoWatchCount.Remaining(AutoWatchDailyLimit, Now);

    private void CountAutoWatched(int n)
    {
        if (n <= 0) return;
        _settings.AutoWatchCount.Add(n, Now);
        SaveSettings();
        OnPropertyChanged(nameof(AutoWatchStatus));
    }

    /// <summary>"Watch on preview": the user lingered on an unwatched story.</summary>
    private async Task AutoWatchSelectedAsync()
    {
        var row = SelectedStory;
        if (row is null || row.MarkedSeen || row.IsHighlight || !AutoWatchOnPreview || IsAutoWatching || AutoWatchBudget == 0) return;
        if (IsExcluded(row.OwnerId) || IsQuietNow) return;
        var n = await WatchAsync([ToTarget(row)], WatchMode.Preview);
        CountAutoWatched(n);
        UpdateTraySeen(row.Username);
    }

    /// <summary>
    /// Background auto-watch: plays the unseen stories of followed accounts in the corner window, within
    /// the daily limit, outside quiet hours and never for excluded accounts. Stories newer than the
    /// reel's "seen" marker are the unseen ones.
    /// </summary>
    private const int AutoWatchChunk = 10;
    private CancellationTokenSource? _autoWatchCts;

    // Live progress of the running auto-watch (shown on the dashboard).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoWatchProgressText), nameof(AutoWatchRemaining))]
    private int _autoWatchDone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoWatchProgressText), nameof(AutoWatchRemaining))]
    private int _autoWatchTotal;

    public int AutoWatchRemaining => Math.Max(0, AutoWatchTotal - AutoWatchDone);

    public string AutoWatchProgressText => AutoWatchTotal == 0
        ? Strings.AutoWatchPreparing
        : string.Format(CultureInfo.CurrentCulture, Strings.AutoWatchProgress, AutoWatchDone, AutoWatchTotal, AutoWatchRemaining);

    /// <summary>Stops the running auto-watch immediately (the setting stays on for the next refresh).</summary>
    [RelayCommand]
    private void StopAutoWatch()
    {
        _autoWatchCts?.Cancel();
        _dialogs.StopWatching();
    }

    /// <summary>
    /// Background auto-watch: plays every unseen story of followed accounts in the corner window, without
    /// any interaction, in chunks of <see cref="AutoWatchChunk"/> (each chunk is verified and recorded before
    /// the next). Bounded by the daily limit, quiet hours and the exclusion list. Stories newer than the
    /// reel's "seen" marker are the unseen ones.
    /// </summary>
    private async Task RunAutoWatchAsync(CancellationToken jobToken)
    {
        if (!AutoWatchNew || IsAutoWatching || _client is null || IsQuietNow) return;
        var budget = AutoWatchBudget;
        if (budget == 0) return;

        var candidates = TrayUsers.Where(u => u.HasUnseen && !IsExcluded(u.UserId))
            .Select(u => new AccountTarget(u.UserId, u.Username)).ToList();
        if (candidates.Count == 0) return;

        _autoWatchCts = CancellationTokenSource.CreateLinkedTokenSource(jobToken, _lifetimeCts.Token);
        var ct = _autoWatchCts.Token;
        AutoWatchDone = 0;
        AutoWatchTotal = 0;
        IsAutoWatching = true;
        var watchedTotal = 0;
        try
        {
            // One reels request per 20 accounts gives the full list (and the total for the progress bar).
            var reels = await _client.GetStoriesForUsersAsync(candidates.Select(t => t.UserId), ct);
            var queue = Planner.UnseenStories(reels, candidates, budget)
                .Select(p => new WatchTarget(p.Account.Username, p.Account.UserId, p.Story!, Stories.FirstOrDefault(r => r.Item.Pk == p.MediaPk)))
                .ToList();
            AutoWatchTotal = queue.Count;
            if (queue.Count == 0) return;
            AppLog.Info($"Auto-watch: {queue.Count} stories from {candidates.Count} accounts");

            foreach (var chunk in queue.Chunk(AutoWatchChunk))
            {
                // Turned off, stopped, or quiet hours started: stop between chunks.
                if (ct.IsCancellationRequested || !AutoWatchNew || IsQuietNow) break;

                var doneBefore = AutoWatchDone;
                var watched = await WatchAsync(chunk, WatchMode.Auto, shownSoFar => AutoWatchDone = doneBefore + shownSoFar);
                AutoWatchDone = doneBefore + chunk.Length; // count skipped/unavailable ones as processed too
                watchedTotal += watched;
                CountAutoWatched(watched);
                MarkFullyWatchedAccounts(chunk);
            }

            if (watchedTotal > 0) _dialogs.Notify(Strings.AutoWatchTitle, string.Format(CultureInfo.CurrentCulture, Strings.AutoWatchDoneMessage, watchedTotal));
        }
        catch (InstagramApiException ex)
        {
            await HandleErrorAsync(ex, silent: true);
        }
        catch (OperationCanceledException)
        {
            // stopped or signed out
        }
        finally
        {
            IsAutoWatching = false;
            AppLog.Info($"Auto-watch finished: {watchedTotal} watched, {AutoWatchDone}/{AutoWatchTotal} processed");
        }
    }

    /// <summary>Accounts whose every queued story counted are no longer shown as having new stories.</summary>
    private void MarkFullyWatchedAccounts(IReadOnlyList<WatchTarget> chunk)
    {
        var recent = History.Take(chunk.Count).ToList();
        foreach (var name in chunk.Select(c => c.Username).Distinct())
        {
            var mine = recent.Where(h => h.Username == name).ToList();
            if (mine.Count == 0 || !mine.All(h => h.Outcome is WatchOutcome.Verified or WatchOutcome.Shown)) continue;
            foreach (var u in TrayUsers.Where(u => u.Username == name)) u.HasUnseen = false;
        }
        UnseenAccounts = TrayUsers.Count(u => u.HasUnseen);
    }

    private void UpdateTraySeen(string username)
    {
        if (Stories.Count == 0 || Stories.Any(s => !s.MarkedSeen)) return;
        var user = TrayUsers.FirstOrDefault(u => u.Username == username);
        if (user is { HasUnseen: true })
        {
            user.HasUnseen = false;
            UnseenAccounts = TrayUsers.Count(u => u.HasUnseen);
        }
    }
}
