using InstagramStoryViewer.Gui.Resources;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>Update checks (at start and every few hours) and one-click install of a new version.</summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    // Minimum spacing between automatic checks, so restarting the app repeatedly does not hammer GitHub.
    // (A once-a-day gate here hid releases published later the same day.)
    private static readonly TimeSpan MinGap = TimeSpan.FromMinutes(10);

    private readonly IMainContext _ctx;
    private readonly IDialogService _dialogs;
    private readonly SettingsService _settings;
    private DispatcherTimer? _timer;
    private string? _announced;

    public UpdatesViewModel(IMainContext ctx, IDialogService dialogs, SettingsService settings)
    {
        _ctx = ctx;
        _dialogs = dialogs;
        _settings = settings;
#pragma warning disable MVVMTK0034
        _checkForUpdates = settings.Current.CheckForUpdates;
#pragma warning restore MVVMTK0034
    }

    public static Version CurrentVersion => typeof(UpdatesViewModel).Assembly.GetName().Version ?? new Version(0, 0);

    [ObservableProperty] private bool _checkForUpdates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private UpdateInfo? _available;

    [ObservableProperty] private string? _status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isInstalling;

    [ObservableProperty] private double _progress;

    public bool HasUpdate => Available is not null;
    public string UpdateText => Available is null ? "" : string.Format(CultureInfo.CurrentCulture, Strings.UpdateAvailable, Available.Version.ToString(3));

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _settings.Current.CheckForUpdates = value;
        _settings.Save();
    }

    /// <summary>Checks now (if due) and then every few hours, in the background.</summary>
    public async Task StartAsync()
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = CheckInterval };
            _timer.Tick += async (_, _) => await CheckIfDueAsync();
            _timer.Start();
        }
        await CheckIfDueAsync();
    }

    private async Task CheckIfDueAsync()
    {
        if (!CheckForUpdates) return;
        if (_settings.Current.LastUpdateCheck is { } last && DateTime.UtcNow - last < MinGap) return;
        await CheckCoreAsync(manual: false);
    }

    [RelayCommand]
    private Task CheckNow() => CheckCoreAsync(manual: true);

    private async Task CheckCoreAsync(bool manual)
    {
        Status = Strings.UpdateChecking;
        var update = await UpdateChecker.CheckAsync(CurrentVersion);
        _settings.Current.LastUpdateCheck = DateTime.UtcNow;
        _settings.Save();
        Available = update;
        Status = update is null ? string.Format(CultureInfo.CurrentCulture, Strings.UpToDate, CurrentVersion.ToString(3)) : UpdateText;
        // Announce a given version only once (the periodic check would otherwise repeat it every few hours).
        if (update is not null && (manual || _announced != update.Tag))
        {
            _announced = update.Tag;
            AppLog.Info($"Update available: {update.Tag}");
            if (!manual) _dialogs.Notify(Strings.UpdateAvailableTitle, string.Format(CultureInfo.CurrentCulture, Strings.UpdateAvailableNotify, UpdateText));
        }
    }

    [RelayCommand]
    private void Open() => _dialogs.OpenUrl(Available?.PageUrl ?? UpdateChecker.ReleasesPage);

    private bool CanInstall() => Available is not null && !IsInstalling;

    /// <summary>Downloads the new exe, checks its SHA-256, swaps it in and restarts the app.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (Available is not { } update) return;
        if (!update.CanInstall)
        {
            Open();
            return;
        }
        if (!_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.UpdateInstallConfirm, update.Version.ToString(3))))
            return;

        IsInstalling = true;
        Progress = 0;
        Status = Strings.UpdateDownloading;
        try
        {
            var path = await UpdateChecker.DownloadAsync(update, CurrentVersion, new Progress<double>(p => Progress = p), CancellationToken.None);
            Status = Strings.UpdateVerifiedRestarting;
            UpdateChecker.LaunchInstaller(path);
            _dialogs.ExitApplication();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException
                                       or InvalidOperationException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("Automatic update failed", ex);
            Status = string.Format(CultureInfo.CurrentCulture, Strings.UpdateFailedStatus, ex.Message);
            _ctx.ShowToast(string.Format(CultureInfo.CurrentCulture, Strings.UpdateFailedToast, ex.Message), isError: true);
            Open();
        }
        finally
        {
            IsInstalling = false;
        }
    }
}
