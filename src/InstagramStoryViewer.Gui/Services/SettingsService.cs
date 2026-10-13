using System.Windows.Threading;
using InstagramStoryViewer.Diagnostics;

namespace InstagramStoryViewer.Gui.Services;

/// <summary>
/// Owns the settings for the whole app. <see cref="Save"/> only marks them dirty; the file is written once
/// things go quiet (half a second after the last change), so a burst of changes or of cookie rotations costs
/// one write instead of dozens. <see cref="Flush"/> writes immediately (exit).
/// </summary>
/// <remarks>
/// Created on the UI thread and saved from it, so the file is never serialized while the UI is changing
/// one of the lists inside the settings.
/// </remarks>
public sealed class SettingsService : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _timer;
    private bool _dirty;

    public SettingsService()
    {
        Current = SettingsStore.Load();
        var fromOldVersion = Current.SchemaVersion < SettingsMigrations.Current;
        PendingHandledImport = SettingsMigrations.Upgrade(Current);
        _timer = new DispatcherTimer { Interval = Quiet };
        _timer.Tick += (_, _) => Flush();
        if (fromOldVersion) Save();
    }

    public AppSettings Current { get; }

    /// <summary>Handled ids found in an old settings file; the history service imports and clears them.</summary>
    public List<string>? PendingHandledImport { get; set; }

    /// <summary>Raised when writing the file failed.</summary>
    public event Action<Exception>? SaveFailed;

    public void Save()
    {
        _dirty = true;
        _timer.Stop();
        _timer.Start();
    }

    public void Flush()
    {
        _timer.Stop();
        if (!_dirty) return;
        _dirty = false;
        try
        {
            SettingsStore.Save(Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Saving settings failed", ex);
            SaveFailed?.Invoke(ex);
        }
    }

    public void Dispose() => Flush();
}
