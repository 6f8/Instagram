using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cinemana.Desktop.Models;
using Cinemana.Desktop.Services;
using Cinemana.Desktop.ViewModels;
using Cinemana.Desktop.Views;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "Cinemana.DownloadUiTests", Guid.NewGuid().ToString("N"));
        var app = new Cinemana.Desktop.App();
        app.InitializeComponent();
        if (args.Length == 2 && args[0] == "--export-icon")
        {
            IconExport.Save((DrawingImage)app.Resources["CinemaMark"], args[1]);
            app.Shutdown();
            return 0;
        }
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var manager = new DownloadManager(new LibraryStore(root));
        var window = new DownloadsWindow(manager, root);
        try
        {
            var content = (FrameworkElement)window.Content;
            void Layout()
            {
                content.Measure(new Size(1060, 780));
                content.Arrange(new Rect(0, 0, 1060, 780));
                content.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            }
            Layout();
            var item = new DownloadItem(new(new(new Movie("test", "Download regression", "", "", "", ""), 720, "mp4", Path.Combine(root, "test.mp4")), DownloadState.Running, 50, 100));
            manager.Items.Add(item);
            Layout();
            var bar = Descendants(content).OfType<ProgressBar>().Single();
            if (bar.Value != 50) throw new Exception("Initial progress was not rendered.");
            item.Progress(new(75, 100, 10));
            Layout();
            if (bar.Value != 75) throw new Exception("Progress update was not rendered.");
            item.State = DownloadState.Paused;
            Layout();
            item.State = DownloadState.Completed;
            item.Progress(new(100, 100, 0));
            Layout();
            if (bar.Value != 100 || bar.IsIndeterminate) throw new Exception("Completed progress was not rendered.");
            Console.WriteLine("PASS: real download row insertion, initial progress, progress updates, paused and completed states.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { window.Close(); app.Shutdown(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
