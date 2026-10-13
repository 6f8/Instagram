using InstagramStoryViewer.Gui.Resources;
using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>Favorites, the auto-watch exclusion list, the Following filter and favorite auto-download.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<AccountRef> FavoriteAccounts { get; private set; } = new();
    public ObservableCollection<AccountRef> ExcludedAccounts { get; private set; } = new();

    // ------------------------------------------------------------------ Following filter

    private ListCollectionView? _followingView;

    /// <summary>The Following page's view of the tray: text search + All / New / Favorites.</summary>
    public ICollectionView FollowingView => _followingView ??= CreateFollowingView();

    [ObservableProperty] private string _followingFilterText = "";
    [ObservableProperty] private string _followingFilterMode = "All";

    partial void OnFollowingFilterTextChanged(string value) => FollowingView.Refresh();
    partial void OnFollowingFilterModeChanged(string value) => FollowingView.Refresh();

    private ListCollectionView CreateFollowingView()
    {
        var view = new ListCollectionView(TrayUsers)
        {
            Filter = o =>
            {
                if (o is not TrayUserViewModel u) return false;
                if (FollowingFilterMode == "New" && !u.HasUnseen) return false;
                if (FollowingFilterMode == "Favorites" && !u.IsFavorite) return false;
                var q = FollowingFilterText.Trim().TrimStart('@');
                return q.Length == 0
                       || u.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
                       || (u.FullName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
            }
        };
        return view;
    }

    private void InitializeAccountLists()
    {
        FavoriteAccounts = new ObservableCollection<AccountRef>(_settings.Favorites);
        ExcludedAccounts = new ObservableCollection<AccountRef>(_settings.ExcludedFromWatch);
        // Fields, not properties: loading saved values must not trigger the change handlers (save / start work).
#pragma warning disable MVVMTK0034
        _autoDownloadFavorites = _settings.AutoDownloadFavorites;
        _notifyFavorites = _settings.NotifyFavorites;
#pragma warning restore MVVMTK0034
    }

    // ------------------------------------------------------------------ favorites / exclusions

    [ObservableProperty] private bool _autoDownloadFavorites;
    [ObservableProperty] private bool _notifyFavorites;

    partial void OnAutoDownloadFavoritesChanged(bool value)
    {
        _settings.AutoDownloadFavorites = value;
        SaveSettings();
        if (value) _jobs.Enqueue("favorites", ProcessFavoritesAsync);
    }

    partial void OnNotifyFavoritesChanged(bool value)
    {
        _settings.NotifyFavorites = value;
        SaveSettings();
    }

    private bool IsFavorite(string userId) => _settings.Favorites.Any(a => a.UserId == userId);

    public bool IsCurrentFavorite => _currentUserId is not null && IsFavorite(_currentUserId);
    public bool IsCurrentExcluded => _currentUserId is not null && IsExcluded(_currentUserId);
    public bool HasCurrentAccount => _currentUserId is not null;

    private void NotifyCurrentAccountFlags()
    {
        OnPropertyChanged(nameof(IsCurrentFavorite));
        OnPropertyChanged(nameof(IsCurrentExcluded));
        OnPropertyChanged(nameof(HasCurrentAccount));
    }

    [RelayCommand]
    private void ToggleFavorite(TrayUserViewModel? user)
    {
        if (user is null) return;
        SetListed(_settings.Favorites, FavoriteAccounts, user.UserId, user.Username, !user.IsFavorite);
        user.IsFavorite = !user.IsFavorite;
        AfterAccountListsChanged();
    }

    [RelayCommand]
    private void ToggleExcluded(TrayUserViewModel? user)
    {
        if (user is null) return;
        SetListed(_settings.ExcludedFromWatch, ExcludedAccounts, user.UserId, user.Username, !user.IsExcluded);
        user.IsExcluded = !user.IsExcluded;
        AfterAccountListsChanged();
    }

    [RelayCommand]
    private void ToggleCurrentFavorite()
    {
        if (_currentUserId is null || CurrentUsername is null) return;
        var on = !IsCurrentFavorite;
        SetListed(_settings.Favorites, FavoriteAccounts, _currentUserId, CurrentUsername, on);
        foreach (var u in TrayUsers.Where(u => u.UserId == _currentUserId)) u.IsFavorite = on;
        AfterAccountListsChanged();
    }

    [RelayCommand]
    private void ToggleCurrentExcluded()
    {
        if (_currentUserId is null || CurrentUsername is null) return;
        var on = !IsCurrentExcluded;
        SetListed(_settings.ExcludedFromWatch, ExcludedAccounts, _currentUserId, CurrentUsername, on);
        foreach (var u in TrayUsers.Where(u => u.UserId == _currentUserId)) u.IsExcluded = on;
        AfterAccountListsChanged();
    }

    [RelayCommand]
    private void RemoveFavorite(AccountRef? account)
    {
        if (account is null) return;
        SetListed(_settings.Favorites, FavoriteAccounts, account.UserId, account.Username, false);
        foreach (var u in TrayUsers.Where(u => u.UserId == account.UserId)) u.IsFavorite = false;
        AfterAccountListsChanged();
    }

    [RelayCommand]
    private void RemoveExcluded(AccountRef? account)
    {
        if (account is null) return;
        SetListed(_settings.ExcludedFromWatch, ExcludedAccounts, account.UserId, account.Username, false);
        foreach (var u in TrayUsers.Where(u => u.UserId == account.UserId)) u.IsExcluded = false;
        AfterAccountListsChanged();
    }

    private static void SetListed(List<AccountRef> store, ObservableCollection<AccountRef> view, string userId, string username, bool on)
    {
        store.RemoveAll(a => a.UserId == userId);
        foreach (var a in view.Where(a => a.UserId == userId).ToList()) view.Remove(a);
        if (!on) return;
        var entry = new AccountRef { UserId = userId, Username = username };
        store.Add(entry);
        view.Add(entry);
    }

    private void AfterAccountListsChanged()
    {
        SaveSettings();
        NotifyCurrentAccountFlags();
        FollowingView.Refresh();
    }

    /// <summary>Usernames can change; keep the stored lists readable.</summary>
    private void RememberUsername(string userId, string username)
    {
        foreach (var a in _settings.Favorites.Concat(_settings.ExcludedFromWatch).Where(a => a.UserId == userId && a.Username != username))
            a.Username = username;
    }

    // ------------------------------------------------------------------ favorites: notify + auto-download

    private bool _favoritesBusy;

    /// <summary>
    /// For favorites with a story newer than the one last handled: announce it (once) and, when enabled,
    /// download every story that is not on disk yet.
    /// </summary>
    private async Task ProcessFavoritesAsync(CancellationToken ct)
    {
        if (_favoritesBusy || _client is null || _downloader is null) return;
        var fresh = TrayUsers
            .Where(u => u.IsFavorite && u.LatestReelMedia > _settings.FavoriteLastNotified.GetValueOrDefault(u.UserId))
            .ToList();
        if (fresh.Count == 0) return;

        _favoritesBusy = true;
        try
        {
            if (NotifyFavorites)
            {
                var names = string.Join(Strings.NameSeparator, fresh.Take(3).Select(u => u.Username)) + (fresh.Count > 3 ? string.Format(CultureInfo.CurrentCulture, Strings.AndMoreSuffix, fresh.Count - 3) : "");
                _dialogs.Notify(Strings.FavoriteNewStoryTitle, names);
            }

            var downloaded = 0;
            if (AutoDownloadFavorites)
            {
                var reels = await _client.GetStoriesForUsersAsync(fresh.Select(u => u.UserId), ct);
                foreach (var reel in reels)
                {
                    var user = fresh.FirstOrDefault(u => u.UserId == (reel.User?.Pk ?? reel.Id));
                    if (user is null) continue;
                    foreach (var item in reel.Items!)
                    {
                        if (_downloader.FindExistingPath(user.Username, item) is not null) continue;
                        try
                        {
                            await _downloader.DownloadAsync(user.Username, item, ct);
                            downloaded++;
                        }
                        catch (Exception ex) when (ex is InstagramApiException or HttpRequestException or IOException)
                        {
                            AppLog.Warn($"Favorite auto-download failed {user.Username}/{item.Pk}: {ex.Message}");
                        }
                    }
                }
                if (downloaded > 0)
                {
                    RefreshDownloads();
                    AppLog.Info($"Favorites auto-download: {downloaded} new files");
                    _dialogs.Notify(Strings.AutoDownloadTitle, string.Format(CultureInfo.CurrentCulture, Strings.FavoritesDownloadedMessage, downloaded));
                }
            }

            foreach (var u in fresh) _settings.FavoriteLastNotified[u.UserId] = u.LatestReelMedia;
            SaveSettings();
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
            _favoritesBusy = false;
        }
    }
}
