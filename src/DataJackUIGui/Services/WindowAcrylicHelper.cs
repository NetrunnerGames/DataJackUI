using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DataJackUIGui.Services;

public static class WindowAcrylicHelper
{
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private enum WindowCompositionAttribute
    {
        WCA_ACCENT_POLICY = 19
    }

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_ENABLE_HOSTBACKDROP = 5,
        ACCENT_INVALID_STATE = 6
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public uint AccentFlags;
        public uint GradientColor;
        public uint AnimationId;
    }

    /// <summary>
    /// Returns true if the operating system does not support native DWM system backdrops
    /// (DWMWA_SYSTEMBACKDROP_TYPE requires Windows 11 22H2 Build 22621+).
    /// Covers all Windows 10 versions and Windows 11 21H2.
    /// </summary>
    public static bool NeedsLegacyAcrylic
    {
        get
        {
            var version = Environment.OSVersion.Version;
            return version.Major < 10 || version.Build < 22621;
        }
    }

    public static int EnableAcrylic(IntPtr hwnd, uint gradientColor = 0x660F0A09)
    {
        if (hwnd == IntPtr.Zero) return -1;

        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2,
            GradientColor = gradientColor,
            AnimationId = 0
        };

        int accentSize = Marshal.SizeOf(accent);
        IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = accentSize
            };
            int res = SetWindowCompositionAttribute(hwnd, ref data);
            if (res == 0)
            {
                // Fallback to basic blur-behind if Acrylic is unsupported on the specific build
                accent.AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND;
                Marshal.StructureToPtr(accent, accentPtr, false);
                res = SetWindowCompositionAttribute(hwnd, ref data);
            }
            return res;
        }
        catch
        {
            return -1;
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    public static int SetDragState(IntPtr hwnd, uint gradientColor = 0x990F0A09)
    {
        if (hwnd == IntPtr.Zero) return -1;

        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_ENABLE_TRANSPARENTGRADIENT,
            AccentFlags = 2,
            GradientColor = gradientColor,
            AnimationId = 0
        };

        int accentSize = Marshal.SizeOf(accent);
        IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = accentSize
            };
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch
        {
            return -1;
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    public static void Apply(Window window, uint gradientColor = 0x660F0A09)
    {
        if (!NeedsLegacyAcrylic) return;

        void ApplyInternal()
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero) return;

            window.Background = Brushes.Transparent;
            EnableAcrylic(helper.Handle, gradientColor);

            var source = HwndSource.FromHwnd(helper.Handle);
            source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WM_ENTERSIZEMOVE)
                {
                    SetDragState(hwnd, gradientColor);
                }
                else if (msg == WM_EXITSIZEMOVE)
                {
                    EnableAcrylic(hwnd, gradientColor);
                }
                return IntPtr.Zero;
            });
        }

        window.StateChanged += (_, _) =>
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle != IntPtr.Zero)
            {
                EnableAcrylic(helper.Handle, gradientColor);
            }
        };

        if (window.IsLoaded)
        {
            ApplyInternal();
        }
        else
        {
            window.SourceInitialized += (_, _) => ApplyInternal();
        }
    }
}
