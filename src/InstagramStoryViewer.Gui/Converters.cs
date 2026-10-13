using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InstagramStoryViewer.Gui;

/// <summary>
/// URL or local path → BitmapImage, decoded at a small size when ConverterParameter gives a pixel width.
/// Remote images download asynchronously; results are cached so scrolling does not refetch them.
/// </summary>
public sealed class ImageSourceConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, BitmapImage> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return null;
        var width = parameter is string p && int.TryParse(p, out var w) ? w : 0;
        var key = width + "|" + s;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            if (width > 0) bmp.DecodePixelWidth = width;
            // OnLoad releases local files right away so they can be deleted or moved.
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            if (Cache.Count > 600) Cache.Clear();
            Cache[key] = bmp;
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value switch
        {
            bool v => v,
            int i => i != 0,
            string s => !string.IsNullOrEmpty(s),
            null => false,
            _ => true
        };
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound value equals ConverterParameter (used for page switching).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Two-way bridge between an enum property and a RadioButton's IsChecked.</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string p ? Enum.Parse(targetType, p) : Binding.DoNothing;
}

/// <summary>Picks a stable colour for an avatar placeholder from the username.</summary>
public sealed class NameToBrushConverter : IValueConverter
{
    private static readonly Brush[] Palette =
    [
        Frozen("#C13584"), Frozen("#405DE6"), Frozen("#E1306C"), Frozen("#FD1D1D"),
        Frozen("#5851DB"), Frozen("#833AB4"), Frozen("#F77737"), Frozen("#3BA776")
    ];

    private static Brush Frozen(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        var hash = 0;
        foreach (var c in s) hash = unchecked(hash * 31 + c);
        return Palette[(int)((uint)hash % Palette.Length)];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Bool → story ring brush (gradient when there is something new to watch).</summary>
public sealed class RingBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current.FindResource(value is true ? "StoryRingBrush" : "SeenRingBrush");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
