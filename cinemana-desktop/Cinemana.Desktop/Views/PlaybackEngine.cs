using LibVLCSharp.Shared;
using System.IO;

namespace Cinemana.Desktop.Views;

internal static class PlaybackEngine
{
    private static readonly Lazy<Task<LibVLC>> Engine = new(() => Task.Run(() =>
    {
        Core.Initialize();
        var options = new List<string> { "--quiet", "--no-video-title-show" };
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "plugins", "plugins.dat"))) options.Add("--reset-plugins-cache");
        return new LibVLC(options.ToArray());
    }));
    // Load native plugins off the UI thread once and reuse them for every title.
    public static Task<LibVLC> Ready => Engine.Value;
    public static void Shutdown()
    {
        if (!Engine.IsValueCreated) return;
        _ = Engine.Value.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); }, TaskScheduler.Default);
    }
}
