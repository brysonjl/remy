using System.Windows;
using System.Windows.Media;
using Remy.App.ViewModels;

namespace Remy.App.Overlay;

/// <summary>Per-size numbers from parity/ui.md 4.3 (OverlaySize.swift).</summary>
public sealed class OverlayMetrics
{
    public double Width, FontSize, LineSpacing, HPad, VPad, CornerRadius, WaveformHeight, HeaderHeight, HeaderSpacing, TextWaveSpacing;
    public double WaveformOnlyWidth, WaveformOnlyHeight, HeaderFont, LineHeight;
    public int MaxLines, CharacterLimit;

    public double MaxTextHeight => MaxLines * LineHeight + (MaxLines - 1) * LineSpacing;
    public double PeekHeight => LineSpacing + LineHeight / 2;
    public double ViewportMax => MaxTextHeight + PeekHeight;
    public double Chrome => HeaderHeight + HeaderSpacing + WaveformHeight + TextWaveSpacing + 2 * VPad;
    public double MinHeight => LineHeight + Chrome;
    public double Height => ViewportMax + Chrome;
    public double CanvasWidth => Math.Max(Width, 300) + 40;
    public double CanvasHeight => Math.Max(130, Height + 24 + 10);

    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");

    public static OverlayMetrics For(OverlaySize s)
    {
        var m = s switch
        {
            OverlaySize.Compact => new OverlayMetrics { Width = 169, FontSize = 10.1, LineSpacing = 3, MaxLines = 2, HPad = 10, VPad = 10, CornerRadius = 14, WaveformHeight = 16, HeaderHeight = 12, HeaderSpacing = 5, TextWaveSpacing = 9, CharacterLimit = 208, WaveformOnlyWidth = 85, WaveformOnlyHeight = 29, HeaderFont = 8 },
            OverlaySize.Large => new OverlayMetrics { Width = 460, FontSize = 15.3, LineSpacing = 4, MaxLines = 4, HPad = 18, VPad = 13.5, CornerRadius = 18, WaveformHeight = 20, HeaderHeight = 16, HeaderSpacing = 7, TextWaveSpacing = 14, CharacterLimit = 640, WaveformOnlyWidth = 220.8, WaveformOnlyHeight = 42, HeaderFont = 9 },
            _ => new OverlayMetrics { Width = 340, FontSize = 13.5, LineSpacing = 4, MaxLines = 3, HPad = 16, VPad = 12, CornerRadius = 16, WaveformHeight = 16, HeaderHeight = 16, HeaderSpacing = 7, TextWaveSpacing = 12, CharacterLimit = 400, WaveformOnlyWidth = 163.2, WaveformOnlyHeight = 38, HeaderFont = 9 },
        };
        m.LineHeight = Math.Round(UiFont.LineSpacing * m.FontSize);
        return m;
    }
}
