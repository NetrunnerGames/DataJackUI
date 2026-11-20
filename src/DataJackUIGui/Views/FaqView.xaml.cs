using System.Windows.Controls;
using DataJackUIGui.ViewModels;

namespace DataJackUIGui.Views;

public partial class FaqView : UserControl
{
    public FaqView(FaqViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
