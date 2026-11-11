using Wpf.Ui.Controls;

namespace DataJackUIGui.Models;

public class CloudProviderOption
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public SymbolRegular Icon { get; set; } = SymbolRegular.Cloud24;
    public string Description { get; set; } = "";
    public bool IsEnabled { get; set; } = true;

    public override string ToString() => DisplayName;
}
