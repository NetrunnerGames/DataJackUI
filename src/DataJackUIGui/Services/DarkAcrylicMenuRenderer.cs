using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DataJackUIGui.Services;

/// <summary>
/// Dark acrylic / Fluent style renderer for Windows Forms ContextMenuStrip,
/// matching standard Windows 11 context menu geometry, acrylic backdrop, item height,
/// margins, border, and clean typography.
/// </summary>
public sealed class DarkAcrylicMenuRenderer : ToolStripProfessionalRenderer
{
    // Exact Windows 11 context menu dark palette (sampled from native taskbar menu)
    private static readonly Color BgColor = Color.FromArgb(242, 44, 44, 44);         // #2C2C2C native dark acrylic background
    private static readonly Color ItemHoverBg = Color.FromArgb(255, 66, 66, 66);      // #424242 native Windows 11 hover fill
    private static readonly Color BorderColor = Color.FromArgb(200, 56, 56, 56);      // subtle outer menu border
    private static readonly Color SeparatorColor = Color.FromArgb(160, 64, 64, 64);   // divider line
    private static readonly Color TextColor = Color.FromArgb(255, 255, 255);          // crisp white text
    private static readonly Color DisabledTextColor = Color.FromArgb(130, 130, 130);

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
    /// Enables native DWM dark mode, rounded corners (8px), and acrylic blur-behind matching
    /// standard Windows 11 context menus.
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

            // 4. Accent Policy for Windows 10 / 11 Acrylic blur behind
            var accent = new AccentPolicy
            {
                AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
                AccentFlags = 2,
                GradientColor = 0xD92C2C2C,
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

        // Native Windows 11 hover item: 6px margin from left and right edges, 1px top/bottom, 4px corner radius, solid #424242 fill (no cyan border)
        var bounds = new Rectangle(6, 1, item.Width - 12, item.Height - 2);
        if (item.Selected && item.Enabled)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(ItemHoverBg);
            using var path = CreateRoundedRectangle(bounds, 4);
            e.Graphics.FillPath(brush, path);
        }
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image is null) return;

        e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Vertically center the 16x16 icon in the item height (native 32px row)
        int y = (e.Item.Height - e.Image.Height) / 2;
        int x = 14; // 14px left margin
        var destRect = new Rectangle(x, y, e.Image.Width, e.Image.Height);

        e.Graphics.DrawImage(e.Image, destRect);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? TextColor : DisabledTextColor;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // Native Windows 11 spacing: 14px margin + 16px icon + 12px gap = 42px text left
        var font = e.TextFont ?? e.Item.Font;
        int textLeft = 42;
        int textWidth = Math.Max(0, e.Item.Width - textLeft - 14);
        int textTop = (e.Item.Height - font.Height) / 2;

        var rect = new Rectangle(textLeft, textTop, textWidth, font.Height + 2);
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
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => ItemHoverBg;
        public override Color ToolStripDropDownBackground => BgColor;
        public override Color ImageMarginGradientBegin => BgColor;
        public override Color ImageMarginGradientMiddle => BgColor;
        public override Color ImageMarginGradientEnd => BgColor;
        public override Color SeparatorDark => SeparatorColor;
        public override Color SeparatorLight => Color.Transparent;
    }
}
