using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Cinemana.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Services.Log.Init(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cinemana.Desktop"));
        // Anything unexpected is logged and shown once instead of closing the app silently.
        DispatcherUnhandledException += (_, args) =>
        {
            Services.Log.Error("ui", args.Exception);
            MessageBox.Show("حدث خطأ غير متوقع وسُجّل في سجل التطبيق. يمكنك نسخ تقرير المشكلة من الإعدادات.", "سينمانا");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception ex) Services.Log.Error("fatal", ex); };
        TaskScheduler.UnobservedTaskException += (_, args) => { Services.Log.Error("task", args.Exception); args.SetObserved(); };
        base.OnStartup(e);
    }
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

/// <summary>
/// Poster image decoded at the size it is shown (ConverterParameter = pixel width) instead of full size,
/// so long pages scroll smoothly and use far less memory. Local files are read once and not kept locked.
/// </summary>
public sealed class PosterConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string source || source.Length == 0 || !Uri.TryCreate(source, UriKind.Absolute, out var uri)) return null;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = int.TryParse(parameter?.ToString(), out int width) ? width : 360;
            if (uri.IsFile) image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            return image;
        }
        catch (Exception e) when (e is System.IO.IOException or NotSupportedException or ArgumentException or UriFormatException) { return null; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
