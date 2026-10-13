using System.Windows;
using System.Windows.Threading;

namespace InstagramStoryViewer.Gui;

public partial class App : Application
{
    // Per Windows user: two copies would race on settings.json and share one embedded-browser profile.
    private static readonly string InstanceId = "InstagramStoryViewer-" + Environment.UserName;

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateSignal;

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
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved(); // never crash on a forgotten task
        base.OnStartup(e);

        var window = new Views.MainWindow();
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

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        InstagramStoryViewer.Diagnostics.AppLog.Error("Unhandled UI exception", e.Exception);
        // Keep the app alive on unexpected errors and tell the user what happened.
        MessageBox.Show(e.Exception.Message, "خطأ غير متوقع", MessageBoxButton.OK, MessageBoxImage.Error,
            MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        e.Handled = true;
    }
}
