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
}

/// <summary>Saved result of the last check (shown again on the next start without new requests).</summary>
public sealed record FollowBackEntry(string Pk, string Username, string? FullName, string? ProfilePicUrl, bool IsVerified);

public sealed record FollowBackSnapshot(DateTime CheckedAt, int Following, int Followers, List<FollowBackEntry> Accounts);

/// <summary>
/// "Not following back": reads both follow lists of the logged-in account and lists the accounts it follows
/// that do not follow it. Runs only when the user presses Check (each 200 accounts cost one request), refuses
/// to start when the hourly request cap would cut it short (a partial followers list would give wrong
/// answers), and keeps the last result on disk.
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
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _hideVerified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(CheckedText), nameof(HasResult))]
    private FollowBackSnapshot? _snapshot;

    public bool HasResult => Snapshot is not null;

    public string Summary => Snapshot is not { } s
        ? Strings.FollowBackNotChecked
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

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        if (_ctx.Client is not { } client || client.ViewerId is not { } me) return;
        if (client.CoolingDownUntil is { } pausedUntil)
        {
            // Paused after Instagram asked to slow down: say until when, without sending anything.
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackPaused, pausedUntil.ToLocalTime()), error: true);
            return;
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsChecking = true;
        Progress = 0;
        SetStatus(Strings.FollowBackReadingCounts, error: false);
        try
        {
            var info = await client.GetCurrentUserAsync(ct);
            int followingTotal = info.FollowingCount ?? 0, followersTotal = info.FollowerCount ?? 0;
            var needed = FollowBack.EstimateRequests(followingTotal, followersTotal);
            var left = client.MaxRequestsPerHour - client.RequestsLastHour;
            if (needed > left)
            {
                SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackOverCap, needed, Math.Max(0, left)), error: true);
                return;
            }
            if (needed > 10 && !_dialogs.Confirm(
                    string.Format(CultureInfo.CurrentCulture, Strings.FollowBackConfirm, followingTotal, followersTotal, needed, left)))
            {
                SetStatus(null, error: false);
                return;
            }

            AppLog.Info($"Follow-back check: following {followingTotal}, followers {followersTotal}, ~{needed} requests");
            var following = await client.GetFollowListAsync(me, InstagramClient.FollowList.Following,
                new Progress<int>(n => Report(Strings.FollowBackListFollowing, n, followingTotal, 0)), ct);
            var followers = await client.GetFollowListAsync(me, InstagramClient.FollowList.Followers,
                new Progress<int>(n => Report(Strings.FollowBackListFollowers, n, followersTotal, 0.5)), ct);

            var result = FollowBack.NotFollowingBack(following, followers)
                .Select(u => new FollowBackEntry(u.Pk!, u.Username ?? "", u.FullName, u.ProfilePicUrl, u.IsVerified == true))
                .ToList();
            Apply(new FollowBackSnapshot(DateTime.Now, following.Count, followers.Count, result));
            SaveSnapshot();
            AppLog.Info($"Follow-back check done: {result.Count} not following back");

            // Instagram sometimes stops a long list early; then some "not following back" may be wrong.
            var incomplete = followers.Count < followersTotal * 0.95 - 5;
            SetStatus(incomplete
                    ? string.Format(CultureInfo.CurrentCulture, Strings.FollowBackIncomplete, followers.Count, followersTotal)
                    : string.Format(CultureInfo.CurrentCulture, Strings.FollowBackDone, result.Count),
                error: incomplete);
        }
        catch (InstagramApiException ex) when (ex.Kind == InstagramErrorKind.RateLimited && client.CoolingDownUntil is { } until)
        {
            AppLog.Warn($"Follow-back check paused by Instagram's rate limit: {ex.RequestPath ?? "?"} {ex.Message}");
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackPaused, until.ToLocalTime()), error: true);
            await _ctx.HandleErrorAsync(ex, silent: true);
        }
        catch (InstagramApiException ex)
        {
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.FollowBackStopped, ex.Message), error: true);
            await _ctx.HandleErrorAsync(ex, silent: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus(Strings.FollowBackCancelled, error: false);
        }
        finally
        {
            IsChecking = false;
            Progress = 0;
        }
    }

    private void Report(string what, int read, int total, double offset)
    {
        Status = total > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.FollowBackReadingOf, what, read, total) : string.Format(CultureInfo.CurrentCulture, Strings.FollowBackReading, what, read);
        Progress = offset + 0.5 * (total > 0 ? Math.Min(1, (double)read / total) : 0);
    }

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
