using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;
using DataJackUIGui.Services;

namespace DataJackUIGui.Views;

/// <summary>
/// Shared UI helper methods for view event handling, navigation commands, and visual tree traversal.
/// </summary>
public static class ViewUiHelper
{
    /// <summary>
    /// Attaches standard markdown and hyperlink navigation commands to a UserControl.
    /// </summary>
    public static void WireHyperlinkCommands(UserControl control)
    {
        control.CommandBindings.Add(new CommandBinding(Markdig.Wpf.Commands.Hyperlink, (_, e) =>
        {
            if (e.Parameter is string url && !string.IsNullOrWhiteSpace(url))
            {
                try { SteamService.OpenUrl(url); } catch { /* ignore bad url */ }
            }
            e.Handled = true;
        }));
        control.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler((_, e) => e.Handled = true));
    }

    /// <summary>
    /// Forwards mouse wheel events up the visual tree when a nested child element swallows them.
    /// </summary>
    public static void ForwardMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement el) return;
        e.Handled = true;
        el.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        });
    }

    /// <summary>
    /// Scrolls a target ScrollViewer (or its descendant) back to the top on the UI dispatcher.
    /// </summary>
    public static void ScrollToTop(Dispatcher dispatcher, DependencyObject container)
    {
        dispatcher.BeginInvoke(() =>
        {
            if (FindDescendant<ScrollViewer>(container) is { } sv) sv.ScrollToTop();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Releases mouse capture/override cursor and executes the detail close action.
    /// </summary>
    public static void DismissDetailFlyout(ICommand? closeCommand)
    {
        Mouse.Capture(null);
        Mouse.OverrideCursor = null;
        closeCommand?.Execute(null);
    }

    /// <summary>
    /// Searches the visual tree for a descendant of type <typeparamref name="T"/>.
    /// </summary>
    public static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
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
}
