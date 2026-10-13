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
    private const int MaxCached = 400;
    private static readonly ConcurrentDictionary<string, BitmapImage> Cache = new();
    private static readonly ConcurrentQueue<string> CacheOrder = new();

    /// <summary>Evicts the oldest entries instead of dropping the whole cache at once.</summary>
    private static void Remember(string key, BitmapImage bmp)
    {
        Cache[key] = bmp;
        CacheOrder.Enqueue(key);
        while (Cache.Count > MaxCached && CacheOrder.TryDequeue(out var oldest))
            Cache.TryRemove(oldest, out _);
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return null;
        var width = parameter is string p && int.TryParse(p, out var w) ? w : 0;
        var key = width + "|" + s;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return null;
        try
        {
            // Remote thumbnails come from the disk cache when possible, and are added to it once downloaded.
            var remote = width > 0 && uri.Scheme is "http" or "https";
            var diskCopy = remote ? Services.ImageDiskCache.TryGet(uri, width) : null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = diskCopy is not null ? new Uri(diskCopy) : uri;
            if (remote && diskCopy is null)
                bmp.DownloadCompleted += (_, _) => Services.ImageDiskCache.Store(uri, width, bmp);
            if (width > 0) bmp.DecodePixelWidth = width;
            // OnLoad releases local files right away so they can be deleted or moved.
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            // A failed download must not stay cached forever (it would leave the image blank until restart).
            bmp.DownloadFailed += (_, _) => Cache.TryRemove(key, out _);
            bmp.DecodeFailed += (_, _) => Cache.TryRemove(key, out _);
            bmp.EndInit();
            Remember(key, bmp);
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

/// <summary>Two-way bridge between a numeric/string property and a RadioButton (ConverterParameter = value).</summary>
public sealed class ValueToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string p
            ? System.Convert.ChangeType(p, Nullable.GetUnderlyingType(targetType) ?? targetType, CultureInfo.InvariantCulture)
            : Binding.DoNothing;
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
