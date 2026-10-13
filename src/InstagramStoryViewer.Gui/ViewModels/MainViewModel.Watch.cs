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
            ShowToast("لا يمكن تعليم الهايلايت كمشاهَد، فقط الستوريات الحالية.");
            return;
        }
        if (IsExcluded(row.OwnerId) &&
            !_dialogs.Confirm($"{row.Username} في قائمة الاستثناء من المشاهدة. هل تريد مشاهدة الستوري رغم ذلك؟"))
            return;
        if (ConfirmBeforeMarkSeen && !_dialogs.Confirm($"سيُفتح الستوري في المتصفح المدمج وسيظهر اسمك في قائمة مشاهديه لدى {row.Username}. متابعة؟"))
            return;
        var ok = await WatchAsync([ToTarget(row)], WatchMode.Manual);
        ShowToast(ok > 0 ? "تمت مشاهدة الستوري" : "لم تتم مشاهدة الستوري (راجع سجل المشاهدات)", isError: ok == 0);
        UpdateTraySeen(row.Username);
    }

    [RelayCommand(CanExecute = nameof(HasCurrentUser))]
    private async Task MarkAllSeenAsync()
    {
        if (_client is null || Stories.Count == 0) return;
        var pending = Stories.Where(s => !s.MarkedSeen && !s.IsHighlight).ToList();
        if (pending.Count == 0)
        {
            ShowToast("كل الستوريات مشاهَدة مسبقاً");
            return;
        }
        if (IsExcluded(pending[0].OwnerId) &&
            !_dialogs.Confirm($"{CurrentUsername} في قائمة الاستثناء من المشاهدة. هل تريد المتابعة رغم ذلك؟"))
            return;
        if (ConfirmBeforeMarkSeen && !_dialogs.Confirm($"ستُعرض {pending.Count} ستوري في المتصفح المدمج وسيظهر اسمك في قائمة مشاهدي {CurrentUsername}. متابعة؟"))
            return;
        var ok = await WatchAsync(pending.Select(ToTarget).ToList(), WatchMode.Manual);
        ShowToast($"تمت مشاهدة {ok} من {pending.Count}", isError: ok < pending.Count);
        if (CurrentUsername is not null) UpdateTraySeen(CurrentUsername);
    }

    private static WatchTarget ToTarget(StoryRowViewModel row) => new(row.Username, row.OwnerId, row.Item, row);

    /// <summary>
    /// Shows the stories in the embedded browser, then asks Instagram whether they now count as seen.
    /// Every attempt is written to the watch history. Returns how many were shown and not contradicted
    /// by Instagram (verified, or unknown when Instagram gave no "seen" marker).
    /// </summary>
    private async Task<int> WatchAsync(IReadOnlyList<WatchTarget> targets, WatchMode mode)
    {
        if (_client is null || targets.Count == 0) return 0;
        var urls = targets.Select(t => StoryUrl(t.Username, t.Item)).ToList();
        AppLog.Info($"Watch start: {targets.Count} stories, mode {mode}");

        var shown = await _dialogs.WatchStoriesAsync(_client.Cookies.ToHeader(), urls, background: mode != WatchMode.Manual);
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
            records.Add(new WatchRecord(DateTime.Now, t.Username, key, mode, outcome));
        }

        WatchHistory.Append(records);
        foreach (var r in records) History.Insert(0, new WatchRecordViewModel(r));
        while (History.Count > 1000) History.RemoveAt(History.Count - 1);

        AppLog.Info($"Watch done: {ok}/{targets.Count} ok; outcomes " +
                    string.Join(", ", records.GroupBy(r => r.Outcome).Select(g => $"{g.Key}={g.Count()}")));
        return ok;
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

    private static string StoryUrl(string username, Models.StoryItem item)
    {
        var pk = item.Pk ?? item.Id?.Split('_')[0];
        return $"https://www.instagram.com/stories/{Uri.EscapeDataString(username)}/{pk}/";
    }

    // ================================================================== auto-watch rules

    private bool IsExcluded(string userId) => _settings.ExcludedFromWatch.Any(a => a.UserId == userId);

    /// <summary>True inside the configured quiet hours (wrapping past midnight, e.g. 1 → 8 or 23 → 7).</summary>
    public bool IsQuietNow
    {
        get
        {
            if (!QuietHoursEnabled || QuietFrom == QuietTo) return false;
            var h = DateTime.Now.Hour;
            return QuietFrom < QuietTo ? h >= QuietFrom && h < QuietTo : h >= QuietFrom || h < QuietTo;
        }
    }

    private void ResetDailyCounterIfNeeded()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_settings.AutoWatchDay == today) return;
        _settings.AutoWatchDay = today;
        _settings.AutoWatchedToday = 0;
    }

    private int AutoWatchBudget => Math.Max(0, AutoWatchDailyLimit - AutoWatchedToday);

    private void CountAutoWatched(int n)
    {
        if (n <= 0) return;
        ResetDailyCounterIfNeeded();
        _settings.AutoWatchedToday += n;
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
    private async Task RunAutoWatchAsync()
    {
        if (!AutoWatchNew || IsAutoWatching || _client is null || IsQuietNow) return;
        var budget = AutoWatchBudget;
        if (budget == 0) return;

        var candidates = TrayUsers.Where(u => u.HasUnseen && !IsExcluded(u.UserId)).Take(20).ToList();
        if (candidates.Count == 0) return;

        IsAutoWatching = true;
        try
        {
            var reels = await _client.GetStoriesForUsersAsync(candidates.Select(t => t.UserId), _lifetimeCts.Token);
            var queue = new List<WatchTarget>();
            foreach (var reel in reels)
            {
                var user = candidates.FirstOrDefault(t => t.UserId == (reel.User?.Pk ?? reel.Id));
                if (user is null) continue;
                var seen = reel.Seen ?? 0;
                foreach (var item in reel.Items!.Where(i => i.TakenAt > seen))
                    queue.Add(new WatchTarget(user.Username, user.UserId, item, Stories.FirstOrDefault(r => r.Item.Pk == item.Pk)));
            }
            queue = queue.Take(budget).ToList();
            if (queue.Count == 0) return;

            var watched = await WatchAsync(queue, WatchMode.Auto);
            CountAutoWatched(watched);

            // Accounts whose every queued story counted are now fully seen.
            var history = History.Take(queue.Count).ToList();
            foreach (var user in candidates)
            {
                var mine = history.Where(h => h.Username == user.Username).ToList();
                if (mine.Count > 0 && mine.All(h => h.Outcome is WatchOutcome.Verified or WatchOutcome.Shown))
                    user.HasUnseen = false;
            }
            UnseenAccounts = TrayUsers.Count(u => u.HasUnseen);
            if (watched > 0) ShowToast($"المشاهدة التلقائية: شوهد {watched} ستوري");
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
            IsAutoWatching = false;
        }
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
