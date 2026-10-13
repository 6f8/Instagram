using InstagramStoryViewer.Gui.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using InstagramStoryViewer.Gui.Services;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>App-level options: notification area and start with Windows (updates: <see cref="UpdatesViewModel"/>).</summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startWithWindows;

    private void InitializeAppOptions()
    {
#pragma warning disable MVVMTK0034 // load saved values without running the change handlers
        _minimizeToTray = _settings.MinimizeToTray;
        _startWithWindows = _settings.StartWithWindows;
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
            ShowToast(Strings.StartupRegistrationFailed, isError: true);
        _settings.StartWithWindows = value;
        SaveSettings();
    }

    /// <summary>Page shortcuts (Ctrl+1 … Ctrl+6).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Navigate(string? page)
    {
        if (IsLoggedIn && Enum.TryParse<AppPage>(page, out var p)) Page = p;
    }
}
