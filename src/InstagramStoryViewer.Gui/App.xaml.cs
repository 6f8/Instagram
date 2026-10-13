using System.Windows;
using System.Windows.Threading;
using InstagramStoryViewer.Automation;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InstagramStoryViewer.Gui;

public partial class App : Application
{
    // Per Windows user: two copies would race on settings.json and share one embedded-browser profile.
    private static readonly string InstanceId = "InstagramStoryViewer-" + Environment.UserName;

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateSignal;
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceId + "-mutex", out var isFirst);
        if (!isFirst)
        {
            // Already running: ask that copy to come to the front, then quit.
            try
            {
                using var signal = EventWaitHandle.OpenExisting(InstanceId + "-activate");
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // the other copy is still starting up; just exit
            }
            Shutdown();
            return;
        }

        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceId + "-activate");
        var listener = new Thread(ListenForActivation) { IsBackground = true, Name = "single-instance" };
        listener.Start();

        InstagramStoryViewer.Diagnostics.AppLog.Initialize(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InstagramStoryViewer", "logs"));
        InstagramStoryViewer.Diagnostics.AppLog.Info(
            $"Start v{typeof(App).Assembly.GetName().Version?.ToString(3)} on {Environment.OSVersion}");

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // Last chance (a crash on a background thread): leave a report behind before the process ends.
            if (args.ExceptionObject is Exception ex) CrashReport.Write(ex, args.IsTerminating ? "background thread (fatal)" : "background thread");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // A task nobody awaited failed: never crash for it, but do not lose the error either.
            AppLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        base.OnStartup(e);

        _host = BuildHost();
        _host.Start();
        _ = Task.Run(ImageDiskCache.Prune);

        var window = new Views.MainWindow(_host.Services);
        MainWindow = window;
        var minimized = e.Args.Contains(Services.StartupRegistration.MinimizedArg);
        if (minimized && window.CanStartHidden)
            window.StartHidden(); // started with Windows: stay in the notification area
        else
            window.Show();
    }

    private void ListenForActivation()
    {
        while (_activateSignal!.WaitOne())
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is not Views.MainWindow w) return;
                w.ShowFromTray(); // also covers a window hidden in the notification area
                w.Topmost = true;  // reliably bring to front, then restore normal z-order
                w.Topmost = false;
            });
        }
    }

    /// <summary>
    /// The app's services, created once and shared (settings, history database, background job queue, clock).
    /// Disposed in reverse order on exit, which flushes pending settings and closes the database.
    /// </summary>
    private static IHost BuildHost() =>
        Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<SettingsService>();
                services.AddSingleton<HistoryService>();
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton(_ => new JobQueue((name, ex) => AppLog.Error($"Background job '{name}' failed", ex)));
            })
            .Build();

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var report = CrashReport.Write(e.Exception, "UI thread");
        // Keep the app alive on unexpected errors and tell the user what happened.
        var details = report is null ? "" : $"\n\nحُفظ تقرير بالتفاصيل في:\n{report}";
        MessageBox.Show(e.Exception.Message + details, "خطأ غير متوقع", MessageBoxButton.OK, MessageBoxImage.Error,
            MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        e.Handled = true;
    }
}
