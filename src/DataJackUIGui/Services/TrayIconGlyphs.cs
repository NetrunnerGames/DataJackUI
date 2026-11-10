using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DataJackUIGui.Services;

/// <summary>
/// Helper to render crisp, pixel-perfect Fluent vector icons for Windows Forms context menu items.
/// Uses standard Segoe MDL2 Assets glyph codepoints rendered with high quality ClearType subpixel grid fitting
/// at 20x20 canvas for crystal-clear legibility and sharpness on high-DPI displays.
/// </summary>
public static class TrayIconGlyphs
{
    private static readonly FontFamily IconFamily = ResolveIconFontFamily();

    // Pure crisp white text and vector iconography
    private static readonly Color DefaultForeground = Color.FromArgb(255, 255, 255);

    private static FontFamily ResolveIconFontFamily()
    {
        try
        {
            using var test = new Font("Segoe MDL2 Assets", 10f);
            if (test.Name == "Segoe MDL2 Assets") return new FontFamily("Segoe MDL2 Assets");
        }
        catch { }

        try
        {
            using var test = new Font("Segoe Fluent Icons", 10f);
            if (test.Name == "Segoe Fluent Icons") return new FontFamily("Segoe Fluent Icons");
        }
        catch { }

        return FontFamily.GenericSansSerif;
    }

    /// <summary>
    /// Renders a native vector icon glyph at 20x20 pixels with high precision subpixel anti-aliasing.
    /// </summary>
    /// <param name="glyph">Unicode glyph string from Segoe MDL2 Assets.</param>
    /// <param name="size">Canvas size in pixels (default 20x20).</param>
    /// <param name="color">Foreground glyph color; defaults to crisp pure white.</param>
    public static Bitmap CreateIcon(string glyph, int size = 20, Color? color = null)
    {
        var fg = color ?? DefaultForeground;

        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // Calibrated 16.5px font size inside 20x20 box gives clean 1.5-2px line strokes without blurring
        using var font = new Font(IconFamily, size * 0.825f, FontStyle.Regular, GraphicsUnit.Pixel);

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
