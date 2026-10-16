using System.Windows;
using TelegramContacts.ViewModels;

namespace TelegramContacts;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Vm.StartAsync();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    // PasswordBox.Password cannot be bound; copy it into the view model by hand.
    private void OnPasswordChanged(object sender, RoutedEventArgs e) => Vm.LoginValue = Password.Password;
}
