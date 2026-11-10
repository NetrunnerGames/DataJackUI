using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DataJackUIGui.Services;

/// <summary>
/// Dark obsidian acrylic / Fluent style renderer for Windows Forms ContextMenuStrip,
/// matching DataJackUI's deep obsidian palette (#101118 / #0D0E15) with high-contrast
/// Windows 11 rounded hover pills (#2E3242) and crisp typography.
/// </summary>
public sealed class DarkAcrylicMenuRenderer : ToolStripProfessionalRenderer
{
    // Deep obsidian dark acrylic palette (distinct from grey hover, matching DataJackUI Obsidian theme)
    private static readonly Color BgColor = Color.FromArgb(244, 14, 15, 22);         // #0E0F16 deep obsidian glass
    private static readonly Color ItemHoverBg = Color.FromArgb(255, 46, 50, 66);      // #2E3242 high-contrast sleek hover fill
    private static readonly Color ItemHoverBorder = Color.FromArgb(60, 6, 182, 212);  // subtle Netrunner cyan accent rim
    private static readonly Color BorderColor = Color.FromArgb(220, 38, 42, 56);      // obsidian border
    private static readonly Color SeparatorColor = Color.FromArgb(160, 32, 35, 48);   // separator line
    private static readonly Color TextColor = Color.FromArgb(255, 255, 255);          // crisp white text
    private static readonly Color DisabledTextColor = Color.FromArgb(120, 125, 140);

    public DarkAcrylicMenuRenderer() : base(new DarkColorTable())
    {
        RoundedEdges = true;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public uint AccentFlags;
        public uint GradientColor;
        public uint AnimationId;
    }

    /// <summary>
    /// Enables native DWM dark mode, rounded corners (8px), and obsidian acrylic blur-behind.
    /// </summary>
    public static void EnableAcrylic(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            // 1. DWM Immersive Dark Mode
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            // 2. DWM Rounded Corners (DWMWCP_ROUND = 2, 8px radius)
            int cornerPref = 2;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

            // 3. DWM System Backdrop Type: Acrylic (DWMSBT_ACRYLIC = 3)
            int backdrop = 3;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            // 4. Accent Policy for Windows 10 / 11 Acrylic blur behind with obsidian tint
            var accent = new AccentPolicy
            {
                AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
                AccentFlags = 2,
                GradientColor = 0xD90E0F16, // Obsidian acrylic tint
                AnimationId = 0
            };
            int accentSize = Marshal.SizeOf(accent);
            IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = 19, // WCA_ACCENT_POLICY
                    Data = accentPtr,
                    SizeOfData = accentSize
                };
                int res = SetWindowCompositionAttribute(hwnd, ref data);
                if (res == 0)
                {
                    accent.AccentState = 3; // ACCENT_ENABLE_BLURBEHIND fallback
                    Marshal.StructureToPtr(accent, accentPtr, false);
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }
        catch { /* gracefully ignore if platform policy denies */ }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.None;
        using var brush = new SolidBrush(BgColor);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(BorderColor, 1);
        var r = new Rectangle(e.AffectedBounds.X, e.AffectedBounds.Y, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        e.Graphics.DrawRectangle(pen, r);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripMenuItem item)
        {
            base.OnRenderMenuItemBackground(e);
            return;
        }

        // Windows 11 hover item: 6px margin from left and right edges, 1px top/bottom, 4px corner radius
        var bounds = new Rectangle(6, 1, item.Width - 12, item.Height - 2);
        if (item.Selected && item.Enabled)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(ItemHoverBg);
            using var path = CreateRoundedRectangle(bounds, 4);
            e.Graphics.FillPath(brush, path);

            using var pen = new Pen(ItemHoverBorder, 1);
            e.Graphics.DrawPath(pen, path);
        }
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image is null) return;

        e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Vertically center the 20x20 icon in the item row matching exact text baseline
        int y = (e.Item.Height - e.Image.Height) / 2;
        int x = 14; // 14px left margin
        var destRect = new Rectangle(x, y, e.Image.Width, e.Image.Height);

        e.Graphics.DrawImage(e.Image, destRect);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? TextColor : DisabledTextColor;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // 14px margin + 20px icon + 12px gap = 46px text left
        // Exact vertical baseline alignment: shift -2px to align font cap-height with icon optical center
        var font = e.TextFont ?? e.Item.Font;
        int textLeft = 46;
        int textWidth = Math.Max(0, e.Item.Width - textLeft - 14);

        var rect = new Rectangle(textLeft, -2, textWidth, e.Item.Height);
        var format = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        TextRenderer.DrawText(e.Graphics, e.Text, font, rect, e.TextColor, format);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(SeparatorColor, 1);
        e.Graphics.DrawLine(pen, 14, y, e.Item.Width - 14, y);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // Don't draw the 3D WinForms gutter margin line
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;
        var arc = new Rectangle(rect.Location, new Size(diameter, diameter));

        // Top left
        path.AddArc(arc, 180, 90);
        // Top right
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        // Bottom right
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        // Bottom left
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => BorderColor;
        public override Color MenuItemBorder => ItemHoverBorder;
        public override Color MenuItemSelected => ItemHoverBg;
        public override Color ToolStripDropDownBackground => BgColor;
        public override Color ImageMarginGradientBegin => BgColor;
        public override Color ImageMarginGradientMiddle => BgColor;
        public override Color ImageMarginGradientEnd => BgColor;
        public override Color SeparatorDark => SeparatorColor;
        public override Color SeparatorLight => Color.Transparent;
    }
}
