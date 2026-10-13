using System.Windows;
using System.Windows.Threading;

namespace InstagramStoryViewer.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive on unexpected errors and tell the user what happened.
        MessageBox.Show(e.Exception.Message, "خطأ غير متوقع", MessageBoxButton.OK, MessageBoxImage.Error,
            MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        e.Handled = true;
    }
}
