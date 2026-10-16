using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TelegramContacts.ViewModels;

public sealed class Inverse : IValueConverter
{
    public static readonly Inverse Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

public sealed class InverseVis : IValueConverter
{
    public static readonly InverseVis Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
