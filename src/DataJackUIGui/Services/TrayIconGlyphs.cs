using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DataJackUIGui.Services;

/// <summary>
/// Helper to render crisp 16x16 Fluent / Segoe icon bitmaps for Windows Forms context menu items.
/// </summary>
public static class TrayIconGlyphs
{
    private static readonly FontFamily IconFamily = ResolveIconFontFamily();

    private static FontFamily ResolveIconFontFamily()
    {
        try
        {
            using var test1 = new Font("Segoe Fluent Icons", 9f);
            if (test1.Name == "Segoe Fluent Icons") return new FontFamily("Segoe Fluent Icons");
        }
        catch { }

        try
        {
            using var test2 = new Font("Segoe MDL2 Assets", 9f);
            if (test2.Name == "Segoe MDL2 Assets") return new FontFamily("Segoe MDL2 Assets");
        }
        catch { }

        return FontFamily.GenericSansSerif;
    }

    /// <summary>
    /// Renders a glyph character into a high-DPI 16x16 32-bit ARGB Bitmap.
    /// </summary>
    public static Bitmap CreateIcon(string glyph, Color color, int size = 16)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var brush = new SolidBrush(color);
        using var font = new Font(IconFamily, size * 0.65f, FontStyle.Regular, GraphicsUnit.Pixel);

        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), sf);
        return bmp;
    }
}
