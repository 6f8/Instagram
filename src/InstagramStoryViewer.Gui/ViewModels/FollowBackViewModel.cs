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
public sealed class FollowBackRowViewModel(FollowBackEntry e)
{
    public FollowBackEntry Entry { get; } = e;
    public string Username => Entry.Username;
    public string? FullName => Entry.FullName;
    public string? ProfilePicUrl => Entry.ProfilePicUrl;
    public bool IsVerified => Entry.IsVerified;
    public string Initial => Username.Length > 0 ? Username[..1].ToUpperInvariant() : "?";
    public bool HasUnseen => false; // the shared avatar template draws a story ring for this
    public string ProfileUrl => $"https://www.instagram.com/{Uri.EscapeDataString(Username)}/";

    /// <summary>Not searched for in the followers list (budget ran out, or a result saved by an older version).</summary>
    public bool IsUnconfirmed => !Entry.Confirmed;
}

/// <summary>Saved result of the last check (shown again on the next start without new requests).</summary>
/// <param name="Confirmed">
/// Searched for in the followers list and not found. Results saved before this check existed load as false,
/// which is right: they were never confirmed.
/// </param>
public sealed record FollowBackEntry(string Pk, string Username, string? FullName, string? ProfilePicUrl, bool IsVerified,
    bool Confirmed = false);

public sealed record FollowBackSnapshot(DateTime CheckedAt, int Following, int Followers, List<FollowBackEntry> Accounts);

/// <summary>
/// "Not following back": reads both follow lists of the logged-in account inside the embedded browser, then
/// confirms every account missing from the followers list by searching for it there (Instagram does not always
/// return the whole followers list, and people who do follow back must never be listed). Runs only when the
/// user presses Check, within the hourly request cap, and keeps the last result on disk.
/// </summary>
public sealed partial class FollowBackViewModel : ObservableObject
{
    private static string CachePath => Path.Combine(SettingsStore.AppDataFolder, "not-following-back.json");

    private readonly IMainContext _ctx;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    public FollowBackViewModel(IMainContext ctx, IDialogService dialogs)
    {
        _ctx = ctx;
        _dialogs = dialogs;
        View = new ListCollectionView(Accounts) { Filter = Matches };
        LoadSnapshot();
    }

    public ObservableCollection<FollowBackRowViewModel> Accounts { get; } = new();

    /// <summary>The list as shown: search text and the "hide verified" option applied.</summary>
    public ICollectionView View { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private bool _isChecking;

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _hideVerified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(CheckedText), nameof(HasResult))]
    private FollowBackSnapshot? _snapshot;

    public bool HasResult => Snapshot is not null;

    public string Summary => Snapshot is not { } s
        ? Strings.FollowBackNotChecked
        : s.Accounts.Count(a => !a.Confirmed) is var unconfirmed and > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.FollowBackSummaryUnconfirmed, s.Following, s.Followers,
                s.Accounts.Count - unconfirmed, unconfirmed)
            : string.Format(CultureInfo.CurrentCulture, Strings.FollowBackSummary, s.Following, s.Followers, s.Accounts.Count);

    public string CheckedText => Snapshot is { } s ? string.Format(CultureInfo.CurrentCulture, Strings.FollowBackCheckedAt, s.CheckedAt) : "";

    partial void OnFilterTextChanged(string value) => View.Refresh();
    partial void OnHideVerifiedChanged(bool value) => View.Refresh();

    private bool Matches(object o)
    {
        if (o is not FollowBackRowViewModel r) return false;
        if (HideVerified && r.IsVerified) return false;
        var q = FilterText.Trim().TrimStart('@');
        return q.Length == 0 || r.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
                             || (r.FullName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Re-evaluates the Check button (login state changed).</summary>
    public void Refresh() => CheckCommand.NotifyCanExecuteChanged();

    private bool CanCheck() => !IsChecking && _ctx.IsLoggedIn;

    /// <summary>Not before this time after Instagram asked to slow down (the pause belongs to the page reads).</summary>
    private DateTime _pausedUntil = DateTime.MinValue;
    private static readonly TimeSpan PauseAfterLimit = TimeSpan.FromMinutes(15);

    private sealed record CheckResult(int Following, int Followers, FollowBackCheck Check);

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
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
            // Everything is read from inside the logged-in instagram.com page, like the site's own followers
            // dialog: Instagram answers follow-list requests from outside the browser with "please wait".
            var r = await _dialogs.WithInstagramPageAsync(client.Cookies.ToHeader(),
                (fetch, pageToken) => RunCheckAsync(client, me, fetch, pageToken));

            static FollowBackEntry Entry(StoryUser u, bool confirmed) =>
                new(u.Pk!, u.Username ?? "", u.FullName, u.ProfilePicUrl, u.IsVerified == true, confirmed);
            var entries = r.Check.NotFollowingBack.Select(u => Entry(u, true))
                .Concat(r.Check.Unverified.Select(u => Entry(u, false)))
                .ToList();
            Apply(new FollowBackSnapshot(DateTime.Now, r.Following, r.Followers, entries));
            SaveSnapshot();
            AppLog.Info($"Follow-back check done: {r.Check.NotFollowingBack.Count} confirmed, {r.Check.Unverified.Count} unverified, " +
                        $"{r.Check.FoundFollowing} found among followers by search");

            SetStatus(r.Check.Unverified.Count > 0
                    ? string.Format(CultureInfo.CurrentCulture, Strings.FollowBackDoneUnconfirmed,
                        r.Check.NotFollowingBack.Count, r.Check.FoundFollowing, r.Check.Unverified.Count)
                    : string.Format(CultureInfo.CurrentCulture, Strings.FollowBackDone, r.Check.NotFollowingBack.Count, r.Check.FoundFollowing),
                error: r.Check.Unverified.Count > 0);
        }
        catch (PageBudgetExceededException ex)
        {
            AppLog.Warn($"Follow-back check: lists need more than the {ex.Budget} requests left this hour");
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackOverBudget, ex.Budget), error: true);
        }
        catch (InstagramApiException ex) when (ex.Kind == InstagramErrorKind.RateLimited)
        {
            _pausedUntil = DateTime.Now + PauseAfterLimit;
            AppLog.Warn($"Follow-back check: Instagram asked to slow down at {ex.RequestPath ?? "?"} (HTTP {(int?)ex.StatusCode})");
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackPaused, _pausedUntil), error: true);
            await _ctx.HandleErrorAsync(ex, silent: true); // saves the response to last-response.txt
        }
        catch (InstagramApiException ex)
        {
            AppLog.Warn($"Follow-back check failed at {ex.RequestPath ?? "?"}: {ex.Kind} HTTP {(int?)ex.StatusCode}");
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackStopped, ex.Message), error: true);
            await _ctx.HandleErrorAsync(ex, silent: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus(Strings.FollowBackCancelled, error: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException
                                       or System.Text.Json.JsonException or KeyNotFoundException)
        {
            // Last resort for embedded-browser failures: report them on the page instead of the crash dialog.
            AppLog.Error("Follow-back check failed in the embedded browser", ex);
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackStopped, ex.Message), error: true);
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// Runs inside the embedded browser, within the requests left this hour (Settings): both follow lists, then a
    /// followers search for every account missing from the followers list. The search is what makes the result
    /// trustworthy: Instagram does not always hand out the complete followers list, and anyone it leaves out
    /// would otherwise be shown as "not following back". (An earlier version also read users/{id}/info, an app
    /// endpoint Instagram answers with 429 when it comes from the website; it is not used.)
    /// </summary>
    private async Task<CheckResult> RunCheckAsync(InstagramClient client, string me,
        Func<string, CancellationToken, Task<PageResult>> fetch, CancellationToken pageToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(pageToken, _cts!.Token);
        var ct = linked.Token;
        var budget = Math.Max(0, client.MaxRequestsPerHour - client.RequestsLastHour);

        AppLog.Info($"Follow-back check (in browser), budget {budget} requests");
        var following = await FollowListReader.ReadAllAsync(me, followers: false, fetch, Task.Delay,
            new Progress<int>(n => Report(Strings.FollowBackListFollowing, n)), ct, budget);
        var followers = await FollowListReader.ReadAllAsync(me, followers: true, fetch, Task.Delay,
            new Progress<int>(n => Report(Strings.FollowBackListFollowers, n)), ct, budget - following.Requests);
        AppLog.Info($"Follow-back lists read: {following.Users.Count} following, {followers.Users.Count} followers, " +
                    $"{following.Requests + followers.Requests} requests");

        var candidates = FollowBack.NotFollowingBack(following.Users, followers.Users);
        var check = await FollowBack.ConfirmAsync(me, candidates, fetch, Task.Delay,
            new Progress<int>(n => Status = string.Format(CultureInfo.CurrentCulture, Strings.FollowBackVerifying, n, candidates.Count)),
            ct, budget - following.Requests - followers.Requests, followers.Users);
        return new CheckResult(following.Users.Count, followers.Users.Count + check.FoundFollowing, check);
    }

    private void Report(string what, int read) =>
        Status = string.Format(CultureInfo.CurrentCulture, Strings.FollowBackReading, what, read);

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void OpenProfile(FollowBackRowViewModel? row)
    {
        if (row is not null) _dialogs.OpenUrl(row.ProfileUrl);
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

    private void SetStatus(string? message, bool error)
    {
        Status = message;
        StatusIsError = error;
    }

    private void Apply(FollowBackSnapshot snapshot)
    {
        Snapshot = snapshot;
        Accounts.Clear();
        foreach (var e in snapshot.Accounts) Accounts.Add(new FollowBackRowViewModel(e));
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
        try
        {
            Directory.CreateDirectory(SettingsStore.AppDataFolder);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(Snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Follow-back cache not saved: {ex.Message}");
        }
    }

    /// <summary>Signing out forgets the result (it belongs to that account).</summary>
    public void Clear()
    {
        _cts?.Cancel();
        Snapshot = null;
        Accounts.Clear();
        SetStatus(null, error: false);
        try { File.Delete(CachePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
