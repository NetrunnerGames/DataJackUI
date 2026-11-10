using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DataJackUIGui.ViewModels;

namespace DataJackUIGui.Views;

public partial class DownloadView : UserControl
{
    private readonly DownloadViewModel _viewModel;
    private HwndSource? _hwndSource;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private DateTime _lastTouchpadScrollTime = DateTime.MinValue;

    public DownloadView(DownloadViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        // Warm the browse sections in the background (best-effort; no-op if already loaded).
        _ = viewModel.LoadBrowseSectionsAsync();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Pull the latest FastFetch value each time the view appears (it's also toggleable in Settings).
        _viewModel.SyncFastFetch();

        if (_hwndSource is null)
        {
            var window = Window.GetWindow(this);
            if (window is not null)
            {
                _hwndSource = PresentationSource.FromVisual(window) as HwndSource;
                _hwndSource?.AddHook(WndProc);
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEHWHEEL)
        {
            short delta = unchecked((short)((long)wParam >> 16));
            var scroller = GetActiveStripScroller();
            if (scroller is not null)
            {
                // Touchpad two-finger horizontal swipe or horizontal tilt wheel:
                // Positive delta means tilted/swiped to the right (advance forward).
                scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + delta);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private ScrollViewer? GetActiveStripScroller()
    {
        if (WeeklyTopSellersScroller?.IsMouseOver == true)
            return WeeklyTopSellersScroller;
        if (LatestMonthHitsScroller?.IsMouseOver == true)
            return LatestMonthHitsScroller;
        return null;
    }

    private bool IsTouchpadVerticalGesture(int delta)
    {
        var now = DateTime.UtcNow;
        // Standard physical mouse wheel notches are multiples of 120 (and >= 120).
        // Precision touchpads generate small fractional/smooth delta values (< 120 or not divisible by 120).
        if (Math.Abs(delta) < 120 || Math.Abs(delta) % 120 != 0)
        {
            _lastTouchpadScrollTime = now;
            return true;
        }

        // If this event arrived within 500ms of a high-resolution touchpad gesture, it's still part of the swipe
        if ((now - _lastTouchpadScrollTime).TotalMilliseconds < 500)
        {
            _lastTouchpadScrollTime = now;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handles mouse wheel events over the horizontal featured strips.
    /// - If swiping vertically with two fingers on a touchpad: forward to MainScrollViewer to scroll the page vertically.
    /// - If rotating a physical USB mouse wheel: translate into horizontal scrolling so the strip can be scrolled.
    /// </summary>
    private void FeaturedStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        if (IsTouchpadVerticalGesture(e.Delta))
        {
            // Touchpad two-finger vertical swipe: scroll the outer page vertically
            if (MainScrollViewer is not null)
            {
                MainScrollViewer.ScrollToVerticalOffset(MainScrollViewer.VerticalOffset - e.Delta);
                e.Handled = true;
            }
            return;
        }

        // Physical USB mouse wheel: scroll the horizontal strip
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>Forward mouse wheel events on inner items grid directly to the outer page ScrollViewer
    /// so scrolling works anywhere over the listings.</summary>
    private void CardScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || MainScrollViewer is null) return;
        MainScrollViewer.ScrollToVerticalOffset(MainScrollViewer.VerticalOffset - (e.Delta * 0.8));
        e.Handled = true;
    }
}
