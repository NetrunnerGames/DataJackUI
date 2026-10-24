using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DataJackUIGui.Services;

/// <summary>
/// Helper to render high-resolution WinUI 3 Fluent / Segoe icon bitmaps for Windows Forms context menu items.
/// Supports clean duotone styling: primary crisp white foreground glyph with subtle Netrunner cyan glass backing glow.
/// </summary>
public static class TrayIconGlyphs
{
    private static readonly FontFamily IconFamily = ResolveIconFontFamily();

    // Netrunner Cyan duotone accent color (#06B6D4) and crisp white foreground
    private static readonly Color DefaultDuotoneAccent = Color.FromArgb(140, 6, 182, 212);
    private static readonly Color DefaultForeground = Color.FromArgb(255, 255, 255, 255);

    private static FontFamily ResolveIconFontFamily()
    {
        try
        {
            using var test1 = new Font("Segoe Fluent Icons", 10f);
            if (test1.Name == "Segoe Fluent Icons") return new FontFamily("Segoe Fluent Icons");
        }
        catch { }

        try
        {
            using var test2 = new Font("Segoe MDL2 Assets", 10f);
            if (test2.Name == "Segoe MDL2 Assets") return new FontFamily("Segoe MDL2 Assets");
        }
        catch { }

        return FontFamily.GenericSansSerif;
    }

    /// <summary>
    /// Renders a duotone WinUI 3 Fluent glyph into a high-DPI 32-bit ARGB Bitmap.
    /// Default size is 24x24 to match modern Windows 11 / classic taskbar context menu standards.
    /// </summary>
    /// <param name="glyph">Primary foreground glyph character.</param>
    /// <param name="size">Icon box dimension in pixels (e.g. 24 or 32).</param>
    /// <param name="accentColor">Optional duotone accent color; defaults to Netrunner cyan.</param>
    /// <param name="fgColor">Optional primary glyph color; defaults to crisp white.</param>
    /// <param name="badgeGlyph">Optional badge / secondary accent glyph character.</param>
    public static Bitmap CreateIcon(
        string glyph,
        int size = 24,
        Color? accentColor = null,
        Color? fgColor = null,
        string? badgeGlyph = null)
    {
        var accent = accentColor ?? DefaultDuotoneAccent;
        var fg = fgColor ?? DefaultForeground;

        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using var font = new Font(IconFamily, size * 0.68f, FontStyle.Regular, GraphicsUnit.Pixel);

        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        // 1. Subtle duotone accent backing aura / disc plate for rich depth
        using (var accentBrush = new SolidBrush(Color.FromArgb(28, accent.R, accent.G, accent.B)))
        {
            float pad = size * 0.08f;
            g.FillEllipse(accentBrush, pad, pad, size - pad * 2, size - pad * 2);
        }

        // 2. Duotone accent drop-shadow / secondary outline
        using (var duotoneBrush = new SolidBrush(accent))
        {
            // Offset slightly down-right for duotone dimensional look
            var shadowRect = new RectangleF(0.8f, 0.8f, size, size);
            g.DrawString(glyph, font, duotoneBrush, shadowRect, sf);
        }

        // 3. Optional secondary badge glyph (e.g. add, dot, or overlay)
        if (!string.IsNullOrEmpty(badgeGlyph))
        {
            using var badgeFont = new Font(IconFamily, size * 0.38f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var badgeBrush = new SolidBrush(accent);
            var badgeRect = new RectangleF(size * 0.45f, size * 0.45f, size * 0.55f, size * 0.55f);
            g.DrawString(badgeGlyph, badgeFont, badgeBrush, badgeRect, sf);
        }

        // 4. Primary crisp white foreground glyph
        using (var fgBrush = new SolidBrush(fg))
        {
            var mainRect = new RectangleF(0f, 0f, size, size);
            g.DrawString(glyph, font, fgBrush, mainRect, sf);
        }

        return bmp;
    }
}
