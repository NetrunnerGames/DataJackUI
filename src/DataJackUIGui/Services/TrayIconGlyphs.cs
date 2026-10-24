using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DataJackUIGui.Services;

/// <summary>
/// Helper to render crisp, pixel-perfect Fluent UI icons for Windows Forms context menu items.
/// Uses standard Segoe MDL2 Assets glyph codepoints that are universally supported across Windows 10/11
/// and render sharp vector geometry at 16x16 size without missing glyph boxes.
/// </summary>
public static class TrayIconGlyphs
{
    private static readonly FontFamily IconFamily = ResolveIconFontFamily();

    // Windows 11 context menu crisp text/icon white (#FFFFFF)
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
    /// Renders a native vector icon glyph matching Windows 11 context menu size and proportions.
    /// Default size is 16x16.
    /// </summary>
    /// <param name="glyph">Unicode glyph string from Segoe MDL2 Assets.</param>
    /// <param name="size">Canvas size in pixels (default 16x16).</param>
    /// <param name="color">Foreground glyph color; defaults to crisp white.</param>
    public static Bitmap CreateIcon(string glyph, int size = 16, Color? color = null)
    {
        var fg = color ?? DefaultForeground;

        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // 13.5px font size for 16x16 icon box produces authentic Windows 11 glyph thickness
        using var font = new Font(IconFamily, size * 0.84f, FontStyle.Regular, GraphicsUnit.Pixel);

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
