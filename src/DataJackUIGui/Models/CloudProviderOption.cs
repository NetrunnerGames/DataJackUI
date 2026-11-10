using System.ComponentModel;

namespace DataJackUIGui.Models;

public class CloudProviderOption
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsEnabled { get; set; } = true;

    public override string ToString() => DisplayName;
}
