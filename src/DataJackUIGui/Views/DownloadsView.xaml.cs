using System.Windows.Controls;
using DataJackUIGui.ViewModels;

namespace DataJackUIGui.Views;

public partial class DownloadsView : UserControl
{
    public DownloadsView(DownloadsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
