using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Cinemana.Desktop.Services;

/// <summary>
/// Dark (design 5) or light palette and the accent colour. Applied at start-up by recolouring the shared
/// brushes in App.xaml, so every StaticResource that uses them follows.
/// </summary>
public static class Theme
{
    public static readonly (string Name, string Hex)[] Accents = [("كهرماني", "#FFB020"), ("أحمر", "#E5193A"), ("أزرق", "#3D8BFF"), ("أخضر", "#2BB673"), ("بنفسجي", "#9B6BFF")];
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Ground"] = "#0E1222", ["Ink"] = "#F5F1E6", ["Muted"] = "#A9AEC4", ["Line"] = "#252B45", ["Numeral"] = "#3A4060",
        ["Surface"] = "#1A2038", ["Panel"] = "#141A30", ["Body"] = "#C9CCDA", ["Faint"] = "#6E7491", ["OnAccent"] = "#0E1222",
    };
    private static readonly Dictionary<string, string> Light = new()
    {
        ["Ground"] = "#F6F3EC", ["Ink"] = "#15182A", ["Muted"] = "#585E74", ["Line"] = "#DDD7CA", ["Numeral"] = "#B9B2A2",
        ["Surface"] = "#E9E3D6", ["Panel"] = "#FFFFFF", ["Body"] = "#2E3247", ["Faint"] = "#7D8296", ["OnAccent"] = "#15182A",
    };
    /// <summary>0 dark, 1 light, 2 follow Windows.</summary>
    public static bool IsLight(int mode) => mode == 1 || mode == 2 && WindowsUsesLight();
    private static bool WindowsUsesLight()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int v && v == 1; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }
    public static void Apply(ResourceDictionary resources, int mode, string accent, bool highContrast)
    {
        var palette = IsLight(mode) ? Light : Dark;
        foreach (var (key, hex) in palette) Recolor(resources, key, hex);
        Recolor(resources, "Accent", accent.Length == 7 ? accent : "#FFB020");
        if (highContrast)
        {
            // Stronger text and lines for low vision.
            Recolor(resources, "Muted", palette["Ink"]); Recolor(resources, "Faint", palette["Body"]);
            Recolor(resources, "Line", IsLight(mode) ? "#7D8296" : "#8A90AE"); Recolor(resources, "Numeral", IsLight(mode) ? "#585E74" : "#A9AEC4");
        }
    }
    private static void Recolor(ResourceDictionary resources, string key, string hex)
    {
        if (resources[key] is not SolidColorBrush brush) return;
        var color = (Color)ColorConverter.ConvertFromString(hex);
        if (brush.IsFrozen) resources[key] = new SolidColorBrush(color); else brush.Color = color;
    }
}
