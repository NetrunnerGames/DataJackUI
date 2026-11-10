using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DataJackUIGui.ViewModels;

namespace DataJackUIGui.Views;

public partial class HomeView : UserControl
{
    private readonly HomeViewModel _viewModel;
    private HwndSource? _hwndSource;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private DateTime _lastTouchpadScrollTime = DateTime.MinValue;

    public HomeView(HomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.LoadAsync();
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
            if (RecentScroller?.IsMouseOver == true)
            {
                RecentScroller.ScrollToHorizontalOffset(RecentScroller.HorizontalOffset + delta);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private bool IsTouchpadVerticalGesture(int delta)
    {
        var now = DateTime.UtcNow;
        if (Math.Abs(delta) < 120 || Math.Abs(delta) % 120 != 0)
        {
            _lastTouchpadScrollTime = now;
            return true;
        }

        if ((now - _lastTouchpadScrollTime).TotalMilliseconds < 500)
        {
            _lastTouchpadScrollTime = now;
            return true;
        }

        return false;
    }

    private void RecentStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        if (IsTouchpadVerticalGesture(e.Delta))
        {
            if (Root is not null)
            {
                Root.ScrollToVerticalOffset(Root.VerticalOffset - e.Delta);
                e.Handled = true;
            }
            return;
        }

        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
