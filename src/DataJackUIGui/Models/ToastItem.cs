using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace DataJackUIGui.Models;

public partial class ToastItem : ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public bool IsError { get; set; }
    public string? ActionLabel { get; set; }
    public Action? OnAction { get; set; }
    public bool HasAction => !string.IsNullOrEmpty(ActionLabel);

    public Brush AccentBrush => new SolidColorBrush(
        IsError ? Color.FromRgb(248, 113, 113) // #f87171 red
        : HasAction ? Color.FromRgb(6, 182, 212) // #06b6d4 Netrunner cyan
        : Color.FromRgb(6, 182, 212)); // #06b6d4 Netrunner cyan

    public Brush BadgeBrush => new SolidColorBrush(
        IsError ? Color.FromArgb(40, 248, 113, 113)
        : HasAction ? Color.FromArgb(40, 6, 182, 212)
        : Color.FromArgb(40, 6, 182, 212));

    public SymbolRegular Icon =>
        IsError ? SymbolRegular.ErrorCircle24
        : HasAction ? SymbolRegular.ArrowSync24
        : SymbolRegular.Info24;

    public IRelayCommand? DismissCommand { get; set; }
    public IRelayCommand? ActionCommand { get; set; }

    internal DispatcherTimer? Timer { get; set; }
}
