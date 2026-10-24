using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DataJackUIGui.Services;

/// <summary>
/// Helper to render crisp, pixel-perfect Fluent UI icons for Windows Forms context menu items.
/// Uses exact Segoe Fluent Icons / Windows 11 Fluent glyph codepoints matching WPF-UI (Home24, Add24, Games24, etc.).
/// Renders in crisp Windows 11 context menu white with subtle subpixel anti-aliasing.
/// </summary>
public static class TrayIconGlyphs
{
    private static readonly FontFamily IconFamily = ResolveIconFontFamily();

    private static readonly Color DefaultForeground = Color.FromArgb(242, 242, 246);

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
    /// Renders a native Fluent UI glyph matching Windows 11 context menu proportions (16x16 crisp icon canvas).
    /// </summary>
    /// <param name="glyph">Unicode glyph string.</param>
    /// <param name="size">Icon box size in pixels (default 16x16 to match Windows 11 context menus).</param>
    /// <param name="color">Foreground glyph color; defaults to crisp Fluent white.</param>
    public static Bitmap CreateIcon(string glyph, int size = 16, Color? color = null)
    {
        var fg = color ?? DefaultForeground;

        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // Font size calibrated for 16px canvas with crisp line weight
        using var font = new Font(IconFamily, size * 0.82f, FontStyle.Regular, GraphicsUnit.Pixel);

        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        using var brush = new SolidBrush(fg);
        var rect = new RectangleF(0f, 0f, size, size);
        g.DrawString(glyph, font, brush, rect, sf);

        return bmp;
    }
}
