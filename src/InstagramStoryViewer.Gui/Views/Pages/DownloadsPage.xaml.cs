using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using InstagramStoryViewer.Gui.ViewModels;

namespace InstagramStoryViewer.Gui.Views.Pages;

public partial class DownloadsPage : UserControl
{
    public DownloadsPage() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void RefreshDownloads_Click(object sender, RoutedEventArgs e) => Vm?.RefreshDownloads();

    private void Downloads_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: DownloadedFileViewModel file })
            Vm?.OpenFileCommand.Execute(file.FullPath);
    }
}
