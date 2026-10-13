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

    [RelayCommand]
    private async Task MarkSeenAsync(StoryRowViewModel? row)
    {
        row ??= SelectedStory;
        if (row is null || _client is null) return;
        if (ConfirmBeforeMarkSeen && !_dialogs.Confirm($"سيُفتح الستوري في المتصفح المدمج وسيظهر اسمك في قائمة مشاهديه لدى {row.Username}. متابعة؟"))
            return;
        var ok = await WatchRowsAsync([row]);
        if (ok > 0) ShowToast("تمت مشاهدة الستوري");
        UpdateTraySeen(row.Username);
    }

    [RelayCommand(CanExecute = nameof(HasCurrentUser))]
    private async Task MarkAllSeenAsync()
    {
        if (_client is null || Stories.Count == 0) return;
        var pending = Stories.Where(s => !s.MarkedSeen).ToList();
        if (pending.Count == 0)
        {
            ShowToast("كل الستوريات مشاهَدة مسبقاً");
            return;
        }
        if (ConfirmBeforeMarkSeen && !_dialogs.Confirm($"ستُعرض {pending.Count} ستوري في المتصفح المدمج وسيظهر اسمك في قائمة مشاهدي {CurrentUsername}. متابعة؟"))
            return;
        var ok = await WatchRowsAsync(pending);
        ShowToast($"تمت مشاهدة {ok} من {pending.Count}", isError: ok < pending.Count);
        if (CurrentUsername is not null) UpdateTraySeen(CurrentUsername);
    }

    private async Task<int> WatchRowsAsync(IReadOnlyList<StoryRowViewModel> rows, bool background = false)
    {
        if (_client is null) return 0;
        var urls = rows.Select(r => StoryUrl(r)).ToList();
        var watched = await _dialogs.WatchStoriesAsync(_client.Cookies.ToHeader(), urls, background);
        for (var i = 0; i < Math.Min(watched, rows.Count); i++)
            rows[i].MarkedSeen = true;
        return watched;
    }

    private static string StoryUrl(StoryRowViewModel row) => StoryUrl(row.Username, row.Item);

    private static string StoryUrl(string username, Models.StoryItem item)
    {
        var pk = item.Pk ?? item.Id?.Split('_')[0];
        return $"https://www.instagram.com/stories/{Uri.EscapeDataString(username)}/{pk}/";
    }

    // ================================================================== auto-watch

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
        if (row is null || row.MarkedSeen || !AutoWatchOnPreview || IsAutoWatching || AutoWatchBudget == 0) return;
        var n = await WatchRowsAsync([row], background: true);
        CountAutoWatched(n);
        UpdateTraySeen(row.Username);
    }

    /// <summary>
    /// Background auto-watch: plays the unseen stories of followed accounts in the corner window, within
    /// the daily limit. Stories newer than the reel's "seen" marker are the unseen ones.
    /// </summary>
    private async Task RunAutoWatchAsync()
    {
        if (!AutoWatchNew || IsAutoWatching || _client is null) return;
        var budget = AutoWatchBudget;
        if (budget == 0) return;

        var targets = TrayUsers.Where(u => u.HasUnseen).Take(20).ToList();
        if (targets.Count == 0) return;

        IsAutoWatching = true;
        try
        {
            var reels = await _client.GetStoriesForUsersAsync(targets.Select(t => t.UserId), _lifetimeCts.Token);
            var queue = new List<(TrayUserViewModel User, string Url)>();
            foreach (var reel in reels)
            {
                var user = targets.FirstOrDefault(t => t.UserId == (reel.User?.Pk ?? reel.Id));
                if (user is null) continue;
                var seen = reel.Seen ?? 0;
                foreach (var item in reel.Items!.Where(i => i.TakenAt > seen))
                    queue.Add((user, StoryUrl(user.Username, item)));
            }
            queue = queue.Take(budget).ToList();
            if (queue.Count == 0) return;

            var watched = await _dialogs.WatchStoriesAsync(_client.Cookies.ToHeader(), queue.Select(q => q.Url).ToList(), background: true);
            CountAutoWatched(watched);

            // Accounts whose every queued story was shown are now fully seen.
            foreach (var group in queue.Take(watched).GroupBy(q => q.User))
                if (queue.Count(q => q.User == group.Key) == group.Count())
                    group.Key.HasUnseen = false;
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
