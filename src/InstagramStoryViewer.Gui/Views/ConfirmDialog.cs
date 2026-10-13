using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InstagramStoryViewer.Gui.Views;

/// <summary>Small dark-themed message box that matches the rest of the app (and reads right-to-left).</summary>
public static class ConfirmDialog
{
    public static bool Show(Window owner, string message) => Build(owner, message, confirm: true);

    public static void Info(Window owner, string message) => Build(owner, message, confirm: false);

    private static bool Build(Window owner, string message, bool confirm)
    {
        var res = Application.Current.Resources;
        var window = new Window
        {
            Owner = owner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FlowDirection = FlowDirection.RightToLeft,
            FontFamily = (FontFamily)res["AppFont"],
            Foreground = (Brush)res["TextBrush"]
        };

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            MaxWidth = 380,
            Margin = new Thickness(0, 0, 0, 22)
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var ok = new Button
        {
            Content = confirm ? "متابعة" : "حسناً",
            Style = (Style)res["PrimaryButton"],
            MinWidth = 96,
            IsDefault = true
        };
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(ok);
        if (confirm)
        {
            var cancel = new Button { Content = "إلغاء", MinWidth = 96, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(cancel);
        }

        window.Content = new Border
        {
            Background = (Brush)res["PanelBrush"],
            BorderBrush = (Brush)res["BorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(26, 22, 26, 20),
            Margin = new Thickness(16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
            Child = new StackPanel { MinWidth = 300, Children = { text, buttons } }
        };
        window.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) window.DragMove();
        };

        return window.ShowDialog() == true;
    }
}
