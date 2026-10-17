using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Cinemana.Desktop;

public partial class App : Application
{
}

/// <summary>True when both bound values are equal as text (active sidebar section).</summary>
public sealed class EqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && values[0]?.ToString() is { } a && a == values[1]?.ToString();
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Carousel dots: true when the item at the dot's position is the shown banner item.</summary>
public sealed class IndexEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && ReferenceEquals(values[0], values[1]);
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Poster height for a card width (2:3 poster, slightly trimmed like the design).</summary>
public sealed class PosterHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double width ? Math.Round(width * 1.3) : 260d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"01", "02"… for an item's position in its list (design 5 numbering). Values: item, list.</summary>
public sealed class RankConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && values[1] is System.Collections.IList list && list.IndexOf(values[0]) is >= 0 and var i ? (i + 1).ToString("00", CultureInfo.InvariantCulture) : "";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
