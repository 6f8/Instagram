using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>App-level options: notification area, start with Windows, update checks.</summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _checkForUpdates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText))]
    private UpdateInfo? _availableUpdate;

    [ObservableProperty] private string? _updateStatus;

    public bool HasUpdate => AvailableUpdate is not null;
    public string UpdateText => AvailableUpdate is null ? "" : $"الإصدار {AvailableUpdate.Version.ToString(3)} متاح";

    private void InitializeAppOptions()
    {
#pragma warning disable MVVMTK0034 // load saved values without running the change handlers
        _minimizeToTray = _settings.MinimizeToTray;
        _startWithWindows = _settings.StartWithWindows;
        _checkForUpdates = _settings.CheckForUpdates;
#pragma warning restore MVVMTK0034
        // Re-register so the entry follows the exe if it was moved or updated.
        if (_settings.StartWithWindows) StartupRegistration.Apply(true);
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        SaveSettings();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!StartupRegistration.Apply(value))
            ShowToast("تعذّر تعديل إعداد التشغيل مع ويندوز.", isError: true);
        _settings.StartWithWindows = value;
        SaveSettings();
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _settings.CheckForUpdates = value;
        SaveSettings();
    }

    private static Version CurrentVersion => typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 0);

    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    // Minimum spacing between automatic checks, so restarting the app repeatedly does not hammer GitHub.
    // (A once-a-day gate here hid releases published later the same day.)
    private static readonly TimeSpan UpdateCheckMinGap = TimeSpan.FromMinutes(10);

    private DispatcherTimer? _updateTimer;
    private string? _announcedUpdate;

    /// <summary>At every start and every few hours while running, in the background.</summary>
    private async Task CheckForUpdatesIfDueAsync()
    {
        if (_updateTimer is null)
        {
            _updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
            _updateTimer.Tick += async (_, _) => await SafeAsync(() => CheckForUpdatesIfDueAsync());
            _updateTimer.Start();
        }
        if (!CheckForUpdates) return;
        if (_settings.LastUpdateCheck is { } last && DateTime.UtcNow - last < UpdateCheckMinGap) return;
        await CheckForUpdatesCoreAsync(manual: false);
    }

    [RelayCommand]
    private Task CheckForUpdatesNow() => CheckForUpdatesCoreAsync(manual: true);

    private async Task CheckForUpdatesCoreAsync(bool manual)
    {
        UpdateStatus = "جاري التحقق...";
        var update = await UpdateChecker.CheckAsync(CurrentVersion);
        _settings.LastUpdateCheck = DateTime.UtcNow;
        SaveSettings();
        AvailableUpdate = update;
        UpdateStatus = update is null ? $"لديك أحدث إصدار ({CurrentVersion.ToString(3)})" : UpdateText;
        // Announce a given version only once (the periodic check would otherwise repeat it every few hours).
        if (update is not null && (manual || _announcedUpdate != update.Tag))
        {
            _announcedUpdate = update.Tag;
            AppLog.Info($"Update available: {update.Tag}");
            if (!manual) _dialogs.Notify("تحديث متاح", $"{UpdateText}. افتح البرنامج لتنزيله.");
        }
    }

    [RelayCommand]
    private void OpenUpdate() => Shell(AvailableUpdate?.PageUrl ?? UpdateChecker.ReleasesPage);
}
