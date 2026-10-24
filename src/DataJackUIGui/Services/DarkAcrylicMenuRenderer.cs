using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DataJackUIGui.Services;

/// <summary>
/// Dark acrylic / Fluent style renderer for Windows Forms ContextMenuStrip,
/// matching modern Windows 11 / classic taskbar context menu proportions and
/// DataJackUI's dark cyberpunk Netrunner cyan glass theme.
/// </summary>
public sealed class DarkAcrylicMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color BgColor = Color.FromArgb(242, 16, 17, 24);         // #101118 dark acrylic glass
    private static readonly Color ItemHoverBg = Color.FromArgb(230, 30, 33, 46);      // #1E212E hover fill
    private static readonly Color ItemHoverBorder = Color.FromArgb(140, 6, 182, 212); // #06B6D4 cyan accent outline
    private static readonly Color BorderColor = Color.FromArgb(220, 42, 45, 62);      // subtle border
    private static readonly Color SeparatorColor = Color.FromArgb(180, 38, 42, 58);   // separator line
    private static readonly Color TextColor = Color.FromArgb(250, 250, 252);          // crisp white text
    private static readonly Color DisabledTextColor = Color.FromArgb(120, 130, 150);

    public DarkAcrylicMenuRenderer() : base(new DarkColorTable())
    {
        RoundedEdges = true;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

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
    /// Enables DWM blur behind / acrylic effect on the ContextMenuStrip popup handle if supported.
    /// </summary>
    public static void EnableAcrylic(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
                AccentFlags = 2,
                GradientColor = 0xD9101118, // ARGB dark acrylic tint
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
                    // Fallback to basic blur behind if acrylic blur behind is unavailable
                    accent.AccentState = 3; // ACCENT_ENABLE_BLURBEHIND
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

        var bounds = new Rectangle(4, 2, item.Width - 8, item.Height - 4);
        if (item.Selected && item.Enabled)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(ItemHoverBg);
            using var path = CreateRoundedRectangle(bounds, 5);
            e.Graphics.FillPath(brush, path);

            using var pen = new Pen(ItemHoverBorder, 1);
            e.Graphics.DrawPath(pen, path);
        }
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image is null) return;

        // Render the icon image crisp with high quality interpolation
        e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Center vertically inside the item's allocated image bounds
        var rect = e.ImageRectangle;
        e.Graphics.DrawImage(e.Image, rect);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? TextColor : DisabledTextColor;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // Shift text cleanly to leave comfortable breathing room after the icon
        var format = e.TextFormat | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        var rect = e.TextRectangle;

        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, rect, e.TextColor, format);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(SeparatorColor, 1);
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
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
