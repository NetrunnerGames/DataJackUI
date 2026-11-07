using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using DataJackUIGui.Services;
using DataJackUIGui.ViewModels;

namespace DataJackUIGui.Views;

public partial class OnlineFixesView : UserControl
{
    private readonly OnlineFixesViewModel _viewModel;

    public OnlineFixesView(OnlineFixesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _viewModel.ScrollToTop = ScrollGridToTop;
        Loaded += async (_, _) => await _viewModel.LoadAsync();

        CommandBindings.Add(new CommandBinding(Markdig.Wpf.Commands.Hyperlink, OpenHyperlink));
        AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler((_, e) => e.Handled = true));
    }

    private static void OpenHyperlink(object sender, ExecutedRoutedEventArgs e)
    {
        Open(e.Parameter as string);
        e.Handled = true;
    }

    private static void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { SteamService.OpenUrl(url); } catch { /* bad/blocked URL. Ignore */ }
    }

    private void Markdown_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement el) return;
        e.Handled = true;
        el.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender,
        });
    }

    private void ScrollGridToTop() => Dispatcher.BeginInvoke(() =>
    {
        if (FindDescendant<ScrollViewer>(GameScroller) is { } sv) sv.ScrollToTop();
    }, System.Windows.Threading.DispatcherPriority.Background);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    private void Scrim_Click(object sender, MouseButtonEventArgs e)
    {
        Mouse.Capture(null);
        Mouse.OverrideCursor = null;
        _viewModel.CloseDetailCommand.Execute(null);
    }

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        Mouse.Capture(null);
        Mouse.OverrideCursor = null;
        _viewModel.CloseDetailCommand.Execute(null);
    }
}
