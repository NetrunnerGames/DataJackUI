using System.Windows.Controls;
using System.Windows.Input;
using DataJackUIGui.ViewModels;

namespace DataJackUIGui.Views;

public partial class OnlineFixesView : UserControl
{
    private readonly OnlineFixesViewModel _vm;

    public OnlineFixesView(OnlineFixesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        _vm.ScrollToTop = () => ViewUiHelper.ScrollToTop(Dispatcher, GameScroller);
        Loaded += async (_, _) => await _vm.LoadAsync();
        ViewUiHelper.WireHyperlinkCommands(this);
    }

    private void Markdown_PreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        ViewUiHelper.ForwardMouseWheel(sender, e);

    private void Scrim_Click(object sender, MouseButtonEventArgs e) =>
        ViewUiHelper.DismissDetailFlyout(_vm.CloseDetailCommand);

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e) =>
        ViewUiHelper.DismissDetailFlyout(_vm.CloseDetailCommand);
}
