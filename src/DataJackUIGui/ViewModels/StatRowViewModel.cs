using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DataJackUIGui.Services.GameData;

namespace DataJackUIGui.ViewModels;

public partial class StatRowViewModel : ObservableObject
{
    private double _original;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(IsValid))]
    private string _text;

    public StatInfo Info { get; }

    public double? Parsed
    {
        get
        {
            if (!double.TryParse(Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var result) &&
                !double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                return null;
            }
            if (!Info.IsFloat && result != Math.Floor(result))
            {
                return null;
            }
            return result;
        }
    }

    public bool IsValid => Parsed.HasValue;

    public bool IsDirty => Parsed.HasValue && Parsed.Value != _original;

    public bool IsReadOnly => Info.Protected;

    public string ApiName => Info.ApiName;

    public string DisplayName => string.IsNullOrWhiteSpace(Info.Name) ? Info.ApiName : Info.Name;

    public StatRowViewModel(StatInfo info)
    {
        Info = info;
        _original = info.Value;
        _text = info.Value.ToString(CultureInfo.CurrentCulture);
    }

    public void Reset()
    {
        Text = _original.ToString(CultureInfo.CurrentCulture);
    }

    public void AcceptChanges()
    {
        if (Parsed.HasValue)
        {
            _original = Parsed.Value;
            OnPropertyChanged(nameof(IsDirty));
        }
    }
}
