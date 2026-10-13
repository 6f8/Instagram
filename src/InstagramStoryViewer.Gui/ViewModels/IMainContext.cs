using InstagramStoryViewer.Api;
using InstagramStoryViewer.Gui.Services;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>
/// What the feature view models (auto-like, comments, updates) need from the main one: the session, the
/// account lists and the shared rules. Keeps them independent of MainViewModel's internals.
/// </summary>
public interface IMainContext
{
    InstagramClient? Client { get; }
    bool IsLoggedIn { get; }
    IReadOnlyCollection<TrayUserViewModel> TrayAccounts { get; }
    IReadOnlyList<AccountRef> FavoriteRefs { get; }
    bool IsExcluded(string userId);
    bool IsQuietNow { get; }

    /// <summary>Local time from the app's clock (replaceable in tests).</summary>
    DateTimeOffset Now { get; }

    /// <summary>Saves the records and shows them at the top of the history page.</summary>
    void AddHistory(IReadOnlyList<WatchRecord> records);

    /// <summary>A story was played by another feature (liking plays it), so mark its row as viewed.</summary>
    void MarkStoryShown(string mediaPk);

    Task HandleErrorAsync(InstagramApiException ex, bool silent);
    void ShowToast(string message, bool isError = false);
    void ScheduleTrayRefresh();
}
