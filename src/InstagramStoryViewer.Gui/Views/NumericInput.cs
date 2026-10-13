using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace InstagramStoryViewer.Gui.Views;

/// <summary>
/// Turns a TextBox bound to an int into a typed number field: digits only, Enter or leaving the box applies it,
/// and the box re-reads the value afterwards so a clamped or rejected entry shows what was actually saved.
/// </summary>
public static class NumericInput
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(NumericInput), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.NewValue is not true) return;
        box.PreviewTextInput += (_, args) => args.Handled = !args.Text.All(char.IsAsciiDigit);
        DataObject.AddPastingHandler(box, (_, args) =>
        {
            if (args.DataObject.GetData(typeof(string)) is not string text || !text.Trim().All(char.IsAsciiDigit))
                args.CancelCommand();
        });
        box.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            Commit(box);
            box.SelectAll();
        };
        box.LostKeyboardFocus += (_, _) => Commit(box);
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
    }

    private static void Commit(TextBox box)
    {
        var binding = BindingOperations.GetBindingExpression(box, TextBox.TextProperty);
        if (binding is null) return;
        binding.UpdateSource();
        binding.UpdateTarget(); // empty/overflowing text or a clamped value → show the stored number again
    }
}
