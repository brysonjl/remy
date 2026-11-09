using System.Windows;
using System.Windows.Media;

namespace Remy.App.Overlay;

/// <summary>Fixed overlay colors (parity/ui.md 4.4), chosen by the overlay's own light/dark.</summary>
public static class OverlayPalette
{
    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>Writes the Ov.* brushes into <paramref name="res"/> (read by the pill via DynamicResource).</summary>
    public static void Apply(ResourceDictionary res, bool dark)
    {
        Color surface = C(dark ? "#102838" : "#FFFDF7"), text = C(dark ? "#F5F2E9" : "#142D3B"), sec = C(dark ? "#B1C7CF" : "#526571"),
              accent = C(dark ? "#75E5D3" : "#08777B"), success = C(dark ? "#90E3BE" : "#22654E"), warm = C(dark ? "#F1B184" : "#AD563B"),
              border = C(dark ? "#355F6A" : "#B6D3CA"), highlight = dark ? Color.FromArgb(0x29, 255, 255, 255) : Color.FromArgb(0xE6, 255, 255, 255);
        Set(res, "Ov.Surface", surface); Set(res, "Ov.Text", text); Set(res, "Ov.Secondary", sec); Set(res, "Ov.Accent", accent);
        Set(res, "Ov.Success", success); Set(res, "Ov.Warm", warm); Set(res, "Ov.Border", border);
        var edge = new LinearGradientBrush(highlight, border, 90); edge.Freeze();
        res["Ov.Edge"] = edge;
        res["Ov.ShadowNear"] = Color.FromArgb((byte)(dark ? 0x52 : 0x1A), 0, 0, 0);
        res["Ov.ShadowFar"] = Color.FromArgb((byte)(dark ? 0x57 : 0x24), 0, 0, 0);
    }

    static void Set(ResourceDictionary r, string key, Color c) { var b = new SolidColorBrush(c); b.Freeze(); r[key] = b; }
}
