using System.Windows;
using System.Windows.Threading;

namespace TelegramContacts;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // WTelegramClient writes its log to the console by default; a WinExe has none.
        WTelegram.Helpers.Log = (_, _) => { };
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "خطأ غير متوقع", MessageBoxButton.OK, MessageBoxImage.Error,
            MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        e.Handled = true;
    }
}
