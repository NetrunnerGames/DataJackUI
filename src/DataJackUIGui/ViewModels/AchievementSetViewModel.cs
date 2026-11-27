using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DataJackUIGui.ViewModels;

public partial class AchievementSetViewModel(string title) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    private bool _isExpanded = true;

    public string Title { get; } = title;

    public List<AchievementRowViewModel> Rows { get; } = new();

    public string Summary => $"{Rows.Count(r => r.Unlocked)}/{Rows.Count}";

    public string ToggleLabel => (IsExpanded ? "▾" : "▸") + " " + Title;

    [RelayCommand]
    private void Toggle()
    {
        IsExpanded = !IsExpanded;
    }

    public void NotifyProgressChanged()
    {
        OnPropertyChanged(nameof(Summary));
    }
}
