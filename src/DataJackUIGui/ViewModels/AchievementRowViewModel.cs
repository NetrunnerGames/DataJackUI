using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DataJackUIGui.Resources;
using DataJackUIGui.Services.GameData;

namespace DataJackUIGui.ViewModels;

public partial class AchievementRowViewModel : ObservableObject
{
    private bool _original;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(IconPath), nameof(ShowProgress))]
    private bool _unlocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconPath))]
    private string? _colorIcon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconPath))]
    private string? _grayIcon;

    public AchievementInfo Info { get; }

    public AchievementSetViewModel Set { get; }

    public bool ShowProgress => !Unlocked && Info.Progress != null;

    public double ProgressMin => Info.Progress?.Min ?? 0.0;

    public double ProgressMax => Info.Progress?.Max ?? 1.0;

    public double ProgressValue => Math.Clamp(Info.Progress?.Value ?? 0.0, ProgressMin, ProgressMax);

    public string ProgressText
    {
        get
        {
            if (Info.Progress == null) return "";
            double val = Math.Clamp(Info.Progress.Value, ProgressMin, ProgressMax);
            return string.Format(CultureInfo.CurrentCulture, "{0:0.#} / {1:0.#}", val, ProgressMax);
        }
    }

    public string ApiName => Info.ApiName;

    public string Name => Info.Name;

    public string Description => Info.Description;

    public bool Hidden => Info.Hidden;

    public string? UnlockedAtText => Info.UnlockedAt?.ToLocalTime().ToString("g");

    public bool IsDirty => Unlocked != _original;

    public string? IconPath => Unlocked ? ColorIcon ?? GrayIcon : GrayIcon ?? ColorIcon;

    public AchievementRowViewModel(AchievementInfo info, AchievementSetViewModel set)
    {
        Info = info;
        Set = set;
        _unlocked = info.Unlocked;
        _original = info.Unlocked;
    }

    public void Reset()
    {
        Unlocked = _original;
    }

    public void AcceptChanges()
    {
        _original = Unlocked;
        OnPropertyChanged(nameof(IsDirty));
    }
}
