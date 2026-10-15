using InstagramStoryViewer.Gui.Resources;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;
using InstagramStoryViewer.Models;
using InstagramStoryViewer.Social;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>An account the user follows that does not follow them back.</summary>
public sealed partial class FollowBackRowViewModel(FollowBackEntry e) : ObservableObject
{
    public FollowBackEntry Entry { get; private set; } = e;
    public string Pk => Entry.Pk;
    public string Username => Entry.Username;
    public string? FullName => Entry.FullName;
    public string? ProfilePicUrl => Entry.ProfilePicUrl;
    public bool IsVerified => Entry.IsVerified;
    public bool IsPrivate => Entry.IsPrivate;
    public string Initial => Username.Length > 0 ? Username[..1].ToUpperInvariant() : "?";
    public bool HasUnseen => false; // the shared avatar template draws a story ring for this
    public string ProfileUrl => $"https://www.instagram.com/{Uri.EscapeDataString(Username)}/";

    /// <summary>Not searched for in the followers list (budget ran out, or a result saved by an older version).</summary>
    public bool IsUnconfirmed => !Entry.Confirmed;

    [ObservableProperty] private bool _isIgnored;
    [ObservableProperty] private bool _isRechecking;

    public void Update(FollowBackEntry entry)
    {
        Entry = entry;
        OnPropertyChanged(nameof(IsUnconfirmed));
    }
}

/// <summary>Someone who started or stopped following, as found by comparing two checks.</summary>
public sealed class FollowerEventRowViewModel(FollowerEvent e)
{
    public FollowerEvent Event { get; } = e;
    public string Username => Event.Account.Username;
    public string? FullName => Event.Account.FullName;
    public string? ProfilePicUrl => Event.Account.ProfilePicUrl;
    public string Initial => Username.Length > 0 ? Username[..1].ToUpperInvariant() : "?";
    public bool HasUnseen => false;
    public string ProfileUrl => $"https://www.instagram.com/{Uri.EscapeDataString(Username)}/";
    public string WhenText => Event.At.ToString("yyyy/MM/dd  HH:mm", CultureInfo.InvariantCulture);
    public string AgoText => Format.Ago(new DateTimeOffset(Event.At));
}

/// <summary>One line of the check history.</summary>
public sealed class CheckPointRowViewModel(CheckPoint p, CheckPoint? previous)
{
    public CheckPoint Point { get; } = p;
    public string WhenText => Point.At.ToString("yyyy/MM/dd  HH:mm", CultureInfo.InvariantCulture);
    public int Followers => Point.Followers;
    public int Following => Point.Following;
    public int NotFollowingBack => Point.NotFollowingBack;
    public string FollowersDelta => Delta(Point.Followers - previous?.Followers);
    public string NotFollowingDelta => Delta(Point.NotFollowingBack - previous?.NotFollowingBack);
    public bool FollowersUp => Point.Followers > (previous?.Followers ?? int.MaxValue);
    public bool FollowersDown => Point.Followers < (previous?.Followers ?? int.MinValue);

    private static string Delta(int? d) => d is null or 0 ? "" : d > 0 ? $"+{d}" : d.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Saved result of the last check (shown again on the next start without new requests).</summary>
/// <param name="Confirmed">
/// Searched for in the followers list and not found. Results saved before this check existed load as false,
/// which is right: they were never confirmed.
/// </param>
public sealed record FollowBackEntry(string Pk, string Username, string? FullName, string? ProfilePicUrl, bool IsVerified,
    bool Confirmed = false, bool IsPrivate = false);

/// <param name="FollowerList">The followers read by this check, compared with the next check (unfollowers, new followers).</param>
/// <param name="ProfileFollowers">Follower count shown on the profile, to see whether the list read was complete.</param>
public sealed record FollowBackSnapshot(DateTime CheckedAt, int Following, int Followers, List<FollowBackEntry> Accounts,
    List<FollowerRef>? FollowerList = null, int? ProfileFollowers = null, int? ProfileFollowing = null);

/// <param name="Gained">True: started following. False: stopped following (confirmed with a followers search).</param>
public sealed record FollowerEvent(DateTime At, FollowerRef Account, bool Gained);

public sealed record CheckPoint(DateTime At, int Followers, int Following, int NotFollowingBack);

/// <summary>Follower changes and check history, kept across checks.</summary>
public sealed class FollowBackJournal
{
    public List<FollowerEvent> Events { get; set; } = new();
    public List<CheckPoint> History { get; set; } = new();
}

/// <summary>
/// "Not following back" and follower changes. A check reads both follow lists of the logged-in account inside
/// the embedded browser, then confirms every account missing from the followers list by searching for it there
/// (Instagram does not always return the whole followers list, and people who do follow back must never be
/// listed); control searches for known followers make sure the search itself works. Comparing with the previous
/// check gives who stopped following (also confirmed by search) and who started. Runs only when the user
/// presses a button, within the hourly request cap; results are kept on disk.
/// </summary>
public sealed partial class FollowBackViewModel : ObservableObject
{
    private const int MaxEvents = 1000;
    private const int MaxHistory = 400;
    private static string CachePath => Path.Combine(SettingsStore.AppDataFolder, "not-following-back.json");
    private static string JournalPath => Path.Combine(SettingsStore.AppDataFolder, "follower-changes.json");

    private readonly IMainContext _ctx;
    private readonly IDialogService _dialogs;
    private readonly SettingsService _settings;
    private CancellationTokenSource? _cts;
    private FollowBackJournal _journal = new();

    public FollowBackViewModel(IMainContext ctx, IDialogService dialogs, SettingsService settings)
    {
        _ctx = ctx;
        _dialogs = dialogs;
        _settings = settings;
        var view = new ListCollectionView(Accounts) { Filter = Matches };
        View = view;
        UnfollowersView = new ListCollectionView(Events) { Filter = o => o is FollowerEventRowViewModel { Event.Gained: false } };
        NewFollowersView = new ListCollectionView(Events) { Filter = o => o is FollowerEventRowViewModel { Event.Gained: true } };
        LoadSnapshot();
        LoadJournal();
        ApplySort();
    }

    public ObservableCollection<FollowBackRowViewModel> Accounts { get; } = new();
    public ObservableCollection<FollowerEventRowViewModel> Events { get; } = new();
    public ObservableCollection<CheckPointRowViewModel> History { get; } = new();

    /// <summary>The list as shown: search, filter, sort, exclusions applied.</summary>
    public ICollectionView View { get; }
    public ICollectionView UnfollowersView { get; }
    public ICollectionView NewFollowersView { get; }

    /// <summary>"NotFollowing", "Unfollowers", "NewFollowers" or "History".</summary>
    [ObservableProperty] private string _tab = "NotFollowing";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(ContinueCommand), nameof(RecheckCommand))]
    private bool _isChecking;

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _hideVerified;

    /// <summary>"All", "Unconfirmed", "Private" or "Public".</summary>
    [ObservableProperty] private string _filterMode = "All";

    /// <summary>"Default" (Instagram's order) or "Name".</summary>
    [ObservableProperty] private string _sortMode = "Default";

    /// <summary>Show the accounts the user excluded (they are hidden otherwise).</summary>
    [ObservableProperty] private bool _showIgnored;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(CheckedText), nameof(HasResult), nameof(ProfileText), nameof(ProfileIncomplete),
        nameof(UnconfirmedCount), nameof(HasUnconfirmed))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private FollowBackSnapshot? _snapshot;

    public bool HasResult => Snapshot is not null;
    public int UnconfirmedCount => Snapshot?.Accounts.Count(a => !a.Confirmed) ?? 0;
    public bool HasUnconfirmed => UnconfirmedCount > 0;
    public int IgnoredCount => _settings.Current.FollowBackIgnored.Count;
    public int UnfollowersCount => Events.Count(e => !e.Event.Gained);
    public int NewFollowersCount => Events.Count(e => e.Event.Gained);

    public string Summary => Snapshot is not { } s
        ? Strings.FollowBackNotChecked
        : UnconfirmedCount is var unconfirmed and > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.FollowBackSummaryUnconfirmed, s.Following, s.Followers,
                s.Accounts.Count - unconfirmed, unconfirmed)
            : string.Format(CultureInfo.CurrentCulture, Strings.FollowBackSummary, s.Following, s.Followers, s.Accounts.Count);

    public string CheckedText => Snapshot is { } s ? string.Format(CultureInfo.CurrentCulture, Strings.FollowBackCheckedAt, s.CheckedAt) : "";

    /// <summary>Profile count next to what was read, so an incomplete list is visible.</summary>
    public string? ProfileText => Snapshot is { ProfileFollowers: { } p } s
        ? string.Format(CultureInfo.CurrentCulture, Strings.FollowBackProfileCounts, p, s.Followers)
        : null;

    public bool ProfileIncomplete => Snapshot is { ProfileFollowers: { } p } s && s.Followers < p * 0.95 - 5;

    partial void OnFilterTextChanged(string value) => View.Refresh();
    partial void OnHideVerifiedChanged(bool value) => View.Refresh();
    partial void OnFilterModeChanged(string value) => View.Refresh();
    partial void OnShowIgnoredChanged(bool value) => View.Refresh();
    partial void OnSortModeChanged(string value) => ApplySort();

    private void ApplySort()
    {
        View.SortDescriptions.Clear();
        if (SortMode == "Name") View.SortDescriptions.Add(new SortDescription(nameof(FollowBackRowViewModel.Username), ListSortDirection.Ascending));
    }

    private bool Matches(object o)
    {
        if (o is not FollowBackRowViewModel r) return false;
        if (r.IsIgnored && !ShowIgnored) return false;
        if (HideVerified && r.IsVerified) return false;
        var mode = FilterMode switch
        {
            "Unconfirmed" => r.IsUnconfirmed,
            "Private" => r.IsPrivate,
            "Public" => !r.IsPrivate,
            _ => true
        };
        if (!mode) return false;
        var q = FilterText.Trim().TrimStart('@');
        return q.Length == 0 || r.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
                             || (r.FullName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Re-evaluates the buttons (login state changed).</summary>
    public void Refresh()
    {
        CheckCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
        RecheckCommand.NotifyCanExecuteChanged();
    }

    private bool CanCheck() => !IsChecking && _ctx.IsLoggedIn;
    private bool CanContinue() => CanCheck() && HasUnconfirmed;
    private bool CanRecheck(FollowBackRowViewModel? row) => CanCheck() && row is not null;

    /// <summary>Not before this time after Instagram asked to slow down (the pause belongs to the page reads).</summary>
    private DateTime _pausedUntil = DateTime.MinValue;
    private static readonly TimeSpan PauseAfterLimit = TimeSpan.FromMinutes(15);

    private sealed record CheckResult(int Following, int Followers, FollowBackCheck Check, List<FollowerRef> FollowerList,
        int? ProfileFollowers, int? ProfileFollowing, List<FollowerEvent> NewEvents);

    // ================================================================== full check

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => RunGuardedAsync(async (client, me) =>
    {
        var previous = Snapshot?.FollowerList;
        var r = await _dialogs.WithInstagramPageAsync(client.Cookies.ToHeader(),
            (fetch, pageToken) => RunCheckAsync(client, me, previous, fetch, pageToken));

        static FollowBackEntry Entry(StoryUser u, bool confirmed) =>
            new(u.Pk!, u.Username ?? "", u.FullName, u.ProfilePicUrl, u.IsVerified == true, confirmed, u.IsPrivate == true);
        var entries = r.Check.NotFollowingBack.Select(u => Entry(u, true))
            .Concat(r.Check.Unverified.Select(u => Entry(u, false)))
            .ToList();
        Apply(new FollowBackSnapshot(DateTime.Now, r.Following, r.Followers, entries, r.FollowerList, r.ProfileFollowers, r.ProfileFollowing));
        SaveSnapshot();
        RecordCheck(r.NewEvents, new CheckPoint(DateTime.Now, r.Followers, r.Following, r.Check.NotFollowingBack.Count));
        AppLog.Info($"Follow-back check done: {r.Check.NotFollowingBack.Count} confirmed, {r.Check.Unverified.Count} unverified, " +
                    $"{r.Check.FoundFollowing} found among followers by search, {r.NewEvents.Count(e => !e.Gained)} unfollowed, " +
                    $"{r.NewEvents.Count(e => e.Gained)} new followers");

        var changes = previous is null ? Strings.FollowBackFirstCheck
            : string.Format(CultureInfo.CurrentCulture, Strings.FollowBackChanges, r.NewEvents.Count(e => !e.Gained), r.NewEvents.Count(e => e.Gained));
        SetStatus((r.Check.Unverified.Count > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.FollowBackDoneUnconfirmed,
                    r.Check.NotFollowingBack.Count, r.Check.FoundFollowing, r.Check.Unverified.Count)
                : string.Format(CultureInfo.CurrentCulture, Strings.FollowBackDone, r.Check.NotFollowingBack.Count, r.Check.FoundFollowing))
            + " " + changes,
            error: r.Check.Unverified.Count > 0);
    });

    /// <summary>
    /// Runs inside the embedded browser, within the requests left this hour (Settings): the profile counts, both
    /// follow lists, a followers search for every account missing from the followers list, and a search for
    /// every follower of the previous check who is missing now. The searches are what make the result
    /// trustworthy: Instagram does not always hand out the complete followers list.
    /// </summary>
    private async Task<CheckResult> RunCheckAsync(InstagramClient client, string me, List<FollowerRef>? previous,
        Func<string, CancellationToken, Task<PageResult>> fetch, CancellationToken pageToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(pageToken, _cts!.Token);
        var ct = linked.Token;
        var budget = Math.Max(0, client.MaxRequestsPerHour - client.RequestsLastHour);

        var (profileFollowers, profileFollowing, used) = await ReadProfileCountsAsync(fetch, ct);
        budget -= used;

        AppLog.Info($"Follow-back check (in browser), budget {budget} requests");
        var following = await FollowListReader.ReadAllAsync(me, followers: false, fetch, Task.Delay,
            new Progress<int>(n => Report(Strings.FollowBackListFollowing, n, profileFollowing)), ct, budget);
        var followers = await FollowListReader.ReadAllAsync(me, followers: true, fetch, Task.Delay,
            new Progress<int>(n => Report(Strings.FollowBackListFollowers, n, profileFollowers)), ct, budget - following.Requests);
        budget -= following.Requests + followers.Requests;
        AppLog.Info($"Follow-back lists read: {following.Users.Count} following, {followers.Users.Count} followers " +
                    $"(profile: {profileFollowing?.ToString(CultureInfo.InvariantCulture) ?? "?"} / {profileFollowers?.ToString(CultureInfo.InvariantCulture) ?? "?"})");

        var candidates = FollowBack.NotFollowingBack(following.Users, followers.Users);
        var check = await FollowBack.ConfirmAsync(me, candidates, fetch, Task.Delay,
            new Progress<int>(n => Status = string.Format(CultureInfo.CurrentCulture, Strings.FollowBackVerifying, n, candidates.Count)),
            ct, budget, followers.Users);
        budget -= check.Requests;

        // Followers found only by the search still follow; add them so the next comparison knows them.
        var found = candidates.Except(check.NotFollowingBack).Except(check.Unverified);
        var followerList = followers.Users.Concat(found)
            .Where(u => u.Pk is not null)
            .Select(u => new FollowerRef(u.Pk!, u.Username ?? "", u.FullName, u.ProfilePicUrl))
            .GroupBy(f => f.Pk).Select(g => g.First())
            .ToList();

        var events = new List<FollowerEvent>();
        if (previous is not null)
        {
            var (lost, gained) = FollowerChanges.Compare(previous, followerList);
            var now = DateTime.Now;
            events.AddRange(gained.Select(g => new FollowerEvent(now, g, Gained: true)));
            if (lost.Count > 0)
            {
                // Missing now is not proof: only those the followers search does not find stopped following.
                var lostUsers = lost.Select(l => new StoryUser { Pk = l.Pk, Username = l.Username }).ToList();
                var confirmLost = await FollowBack.ConfirmAsync(me, lostUsers, fetch, Task.Delay,
                    new Progress<int>(n => Status = string.Format(CultureInfo.CurrentCulture, Strings.FollowBackVerifyingLost, n, lost.Count)),
                    ct, budget, followers.Users);
                var lostIds = confirmLost.NotFollowingBack.Select(u => u.Pk).ToHashSet();
                events.AddRange(lost.Where(l => lostIds.Contains(l.Pk)).Select(l => new FollowerEvent(now, l, Gained: false)));
                // Found by the search, or not searched (budget ran out): kept as followers, so the next check asks again.
                followerList.AddRange(lost.Where(l => !lostIds.Contains(l.Pk)));
            }
        }
        return new CheckResult(following.Users.Count, followers.Users.Count + check.FoundFollowing, check, followerList,
            profileFollowers, profileFollowing, events);
    }

    /// <summary>
    /// The follower and following counts shown on the profile (the website's settings form gives the username,
    /// the profile endpoint the counts). Best effort: two requests, and nothing breaks without them.
    /// </summary>
    private static async Task<(int? Followers, int? Following, int Requests)> ReadProfileCountsAsync(
        Func<string, CancellationToken, Task<PageResult>> fetch, CancellationToken ct)
    {
        var requests = 0;
        try
        {
            requests++;
            var form = FollowListReader.Parse<AccountFormResponse>(await fetch(FollowListReader.AccountFormPath, ct), FollowListReader.AccountFormPath);
            if (form.FormData?.Username is not { Length: > 0 } username) return (null, null, requests);
            var path = FollowListReader.ProfilePath(username);
            requests++;
            var profile = FollowListReader.Parse<WebProfileInfoResponse>(await fetch(path, ct), path);
            return (profile.Data?.User?.EdgeFollowedBy?.Count, profile.Data?.User?.EdgeFollow?.Count, requests);
        }
        catch (InstagramApiException ex) when (ex.Kind is not InstagramErrorKind.RateLimited and not InstagramErrorKind.LoginRequired)
        {
            AppLog.Warn($"Profile counts unavailable: {ex.Kind} {ex.Message}");
            return (null, null, requests);
        }
    }

    private void Report(string what, int read, int? total) =>
        Status = total is > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.FollowBackReadingOfTotal, what, read, total)
            : string.Format(CultureInfo.CurrentCulture, Strings.FollowBackReading, what, read);

    // ================================================================== continue / recheck

    /// <summary>Confirms only the "unconfirmed" accounts of the last check, without reading the lists again.</summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private Task ContinueAsync() => RunGuardedAsync(async (client, me) =>
    {
        if (Snapshot is not { } snap) return;
        var pending = snap.Accounts.Where(a => !a.Confirmed).ToList();
        var check = await _dialogs.WithInstagramPageAsync(client.Cookies.ToHeader(), (fetch, pageToken) =>
            ConfirmEntriesAsync(client, me, pending, snap, fetch, pageToken, Strings.FollowBackVerifying));
        ApplyConfirmation(pending.Select(p => p.Pk).ToList(), check);
        var left = Snapshot?.Accounts.Count(a => !a.Confirmed) ?? 0;
        SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackContinued,
            check.NotFollowingBack.Count, check.FoundFollowing, left), error: left > 0);
    });

    /// <summary>Searches for one account again (two requests: the account and a control).</summary>
    [RelayCommand(CanExecute = nameof(CanRecheck))]
    private Task RecheckAsync(FollowBackRowViewModel? row) => RunGuardedAsync(async (client, me) =>
    {
        if (row is null || Snapshot is not { } snap) return;
        row.IsRechecking = true;
        try
        {
            var check = await _dialogs.WithInstagramPageAsync(client.Cookies.ToHeader(), (fetch, pageToken) =>
                ConfirmEntriesAsync(client, me, [row.Entry], snap, fetch, pageToken, Strings.FollowBackVerifying));
            ApplyConfirmation([row.Pk], check);
            SetStatus(check.FoundFollowing > 0
                    ? string.Format(CultureInfo.CurrentCulture, Strings.FollowBackRecheckFollows, row.Username)
                    : check.NotFollowingBack.Count > 0
                        ? string.Format(CultureInfo.CurrentCulture, Strings.FollowBackRecheckConfirmed, row.Username)
                        : string.Format(CultureInfo.CurrentCulture, Strings.FollowBackRecheckUnknown, row.Username),
                error: check.Unverified.Count > 0);
        }
        finally
        {
            row.IsRechecking = false;
        }
    });

    private async Task<FollowBackCheck> ConfirmEntriesAsync(InstagramClient client, string me, IReadOnlyList<FollowBackEntry> entries,
        FollowBackSnapshot snap, Func<string, CancellationToken, Task<PageResult>> fetch, CancellationToken pageToken, string progressFormat)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(pageToken, _cts!.Token);
        var budget = Math.Max(0, client.MaxRequestsPerHour - client.RequestsLastHour);
        var users = entries.Select(e => new StoryUser { Pk = e.Pk, Username = e.Username }).ToList();
        var known = (snap.FollowerList ?? []).Select(f => new StoryUser { Pk = f.Pk, Username = f.Username }).ToList();
        return await FollowBack.ConfirmAsync(me, users, fetch, Task.Delay,
            new Progress<int>(n => Status = string.Format(CultureInfo.CurrentCulture, progressFormat, n, users.Count)),
            linked.Token, budget, known);
    }

    /// <summary>Marks confirmed accounts, drops the ones the search found among the followers.</summary>
    private void ApplyConfirmation(IReadOnlyCollection<string> checkedPks, FollowBackCheck check)
    {
        if (Snapshot is not { } snap) return;
        var confirmed = check.NotFollowingBack.Select(u => u.Pk).ToHashSet();
        var unverified = check.Unverified.Select(u => u.Pk).ToHashSet();
        // Searched, and neither confirmed nor left unverified: the search found them among the followers.
        var found = checkedPks.Where(pk => !confirmed.Contains(pk) && !unverified.Contains(pk)).ToHashSet();
        var entries = new List<FollowBackEntry>();
        var dropped = new List<FollowBackEntry>();
        foreach (var e in snap.Accounts)
        {
            if (confirmed.Contains(e.Pk)) entries.Add(e with { Confirmed = true });
            else if (found.Contains(e.Pk)) dropped.Add(e);
            else entries.Add(e);
        }
        var followerList = (snap.FollowerList ?? []).Concat(dropped.Select(d => new FollowerRef(d.Pk, d.Username, d.FullName, d.ProfilePicUrl))).ToList();
        Apply(snap with { Accounts = entries, Followers = snap.Followers + dropped.Count, FollowerList = followerList });
        SaveSnapshot();
    }

    /// <summary>Common guard, error handling and status for every action that reads from Instagram.</summary>
    private async Task RunGuardedAsync(Func<InstagramClient, string, Task> action)
    {
        if (_ctx.Client is not { } client || client.ViewerId is not { } me) return;
        if (_pausedUntil > DateTime.Now)
        {
            // Instagram asked to slow down: say until when, without sending anything.
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackPaused, _pausedUntil), error: true);
            return;
        }
        _cts = new CancellationTokenSource();
        IsChecking = true;
        SetStatus(Strings.FollowBackOpening, error: false);
        try
        {
            await action(client, me);
        }
        catch (PageBudgetExceededException ex)
        {
            AppLog.Warn($"Follow-back: lists need more than the {ex.Budget} requests left this hour");
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackOverBudget, ex.Budget), error: true);
        }
        catch (InstagramApiException ex) when (ex.Kind == InstagramErrorKind.RateLimited)
        {
            _pausedUntil = DateTime.Now + PauseAfterLimit;
            AppLog.Warn($"Follow-back: Instagram asked to slow down at {ex.RequestPath ?? "?"} (HTTP {(int?)ex.StatusCode})");
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackPaused, _pausedUntil), error: true);
            await _ctx.HandleErrorAsync(ex, silent: true); // saves the response to last-response.txt
        }
        catch (InstagramApiException ex)
        {
            AppLog.Warn($"Follow-back failed at {ex.RequestPath ?? "?"}: {ex.Kind} HTTP {(int?)ex.StatusCode}");
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackStopped, ex.Message), error: true);
            await _ctx.HandleErrorAsync(ex, silent: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus(Strings.FollowBackCancelled, error: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException
                                       or JsonException or KeyNotFoundException)
        {
            // Last resort for embedded-browser failures: report them on the page instead of the crash dialog.
            AppLog.Error("Follow-back failed in the embedded browser", ex);
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackStopped, ex.Message), error: true);
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    // ================================================================== list actions

    [RelayCommand]
    private void OpenProfile(object? row)
    {
        var url = row switch
        {
            FollowBackRowViewModel r => r.ProfileUrl,
            FollowerEventRowViewModel e => e.ProfileUrl,
            _ => null
        };
        if (url is not null) _dialogs.OpenUrl(url);
    }

    /// <summary>Excludes an account (or brings it back): excluded accounts are hidden in every check.</summary>
    [RelayCommand]
    private void ToggleIgnored(FollowBackRowViewModel? row)
    {
        if (row is null) return;
        var list = _settings.Current.FollowBackIgnored;
        list.RemoveAll(a => a.UserId == row.Pk);
        row.IsIgnored = !row.IsIgnored;
        if (row.IsIgnored) list.Add(new AccountRef { UserId = row.Pk, Username = row.Username });
        _settings.Save();
        OnPropertyChanged(nameof(IgnoredCount));
        View.Refresh();
    }

    /// <summary>Copies the shown usernames, one per line.</summary>
    [RelayCommand]
    private void CopyUsernames()
    {
        var names = View.Cast<FollowBackRowViewModel>().Select(r => r.Username).ToList();
        if (names.Count == 0) return;
        _dialogs.CopyToClipboard(string.Join(Environment.NewLine, names));
        _ctx.ShowToast(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackCopied, names.Count));
    }

    /// <summary>Saves the shown list as a CSV file (opens in Excel; UTF-8 with BOM so Arabic names display).</summary>
    [RelayCommand]
    private void ExportCsv()
    {
        var rows = View.Cast<FollowBackRowViewModel>().ToList();
        if (rows.Count == 0) return;
        var path = _dialogs.PickSaveFile($"not-following-back-{DateTime.Now:yyyyMMdd}.csv", "CSV (*.csv)|*.csv");
        if (path is null) return;
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var lines = new List<string> { "username,full_name,profile_url,verified,private,confirmed" };
        lines.AddRange(rows.Select(r => string.Join(",", Q(r.Username), Q(r.FullName), Q(r.ProfileUrl),
            r.IsVerified ? "yes" : "no", r.IsPrivate ? "yes" : "no", r.IsUnconfirmed ? "no" : "yes")));
        try
        {
            File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _ctx.ShowToast(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackExported, rows.Count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.ShowToast(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackExportFailed, ex.Message), isError: true);
        }
    }

    [RelayCommand]
    private void ClearChanges()
    {
        if (!_dialogs.Confirm(Strings.FollowBackClearChangesConfirm)) return;
        _journal.Events.Clear();
        Events.Clear();
        SaveJournal();
        NotifyEventCounts();
    }

    private void SetStatus(string? message, bool error)
    {
        Status = message;
        StatusIsError = error;
    }

    // ================================================================== storage

    private void Apply(FollowBackSnapshot snapshot)
    {
        Snapshot = snapshot;
        var ignored = _settings.Current.FollowBackIgnored.Select(a => a.UserId).ToHashSet();
        Accounts.Clear();
        foreach (var e in snapshot.Accounts) Accounts.Add(new FollowBackRowViewModel(e) { IsIgnored = ignored.Contains(e.Pk) });
    }

    private void RecordCheck(List<FollowerEvent> events, CheckPoint point)
    {
        _journal.Events.InsertRange(0, events.OrderByDescending(e => e.Gained));
        if (_journal.Events.Count > MaxEvents) _journal.Events.RemoveRange(MaxEvents, _journal.Events.Count - MaxEvents);
        _journal.History.Add(point);
        if (_journal.History.Count > MaxHistory) _journal.History.RemoveRange(0, _journal.History.Count - MaxHistory);
        SaveJournal();
        ShowJournal();
    }

    private void ShowJournal()
    {
        Events.Clear();
        foreach (var e in _journal.Events) Events.Add(new FollowerEventRowViewModel(e));
        History.Clear();
        for (var i = _journal.History.Count - 1; i >= 0; i--)
            History.Add(new CheckPointRowViewModel(_journal.History[i], i > 0 ? _journal.History[i - 1] : null));
        NotifyEventCounts();
    }

    private void NotifyEventCounts()
    {
        OnPropertyChanged(nameof(UnfollowersCount));
        OnPropertyChanged(nameof(NewFollowersCount));
    }

    private void LoadSnapshot()
    {
        try
        {
            if (File.Exists(CachePath) && JsonSerializer.Deserialize<FollowBackSnapshot>(File.ReadAllText(CachePath)) is { } s)
                Apply(s);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Follow-back cache unreadable: {ex.Message}");
        }
    }

    private void SaveSnapshot()
    {
        if (Snapshot is null) return;
        Write(CachePath, JsonSerializer.Serialize(Snapshot));
    }

    private void LoadJournal()
    {
        try
        {
            if (File.Exists(JournalPath) && JsonSerializer.Deserialize<FollowBackJournal>(File.ReadAllText(JournalPath)) is { } j)
                _journal = j;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Follower changes unreadable: {ex.Message}");
        }
        ShowJournal();
    }

    private void SaveJournal() => Write(JournalPath, JsonSerializer.Serialize(_journal));

    private static void Write(string path, string json)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.AppDataFolder);
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Not saved ({Path.GetFileName(path)}): {ex.Message}");
        }
    }

    /// <summary>Signing out forgets the results (they belong to that account).</summary>
    public void Clear()
    {
        _cts?.Cancel();
        Snapshot = null;
        Accounts.Clear();
        _journal = new FollowBackJournal();
        ShowJournal();
        SetStatus(null, error: false);
        foreach (var path in new[] { CachePath, JournalPath })
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
