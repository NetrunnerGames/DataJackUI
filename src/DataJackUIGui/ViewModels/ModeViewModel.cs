using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataJackUIGui.Models;
using DataJackUIGui.Services;

namespace DataJackUIGui.ViewModels;

/// <summary>One card on the Mode page. A single unlocker backend's name, status, and action button.</summary>
public partial class ModeCardViewModel(UnlockerMode mode, string title, string description) : ObservableObject
{
    public UnlockerMode Mode { get; } = mode;
    public string Title { get; } = title;
    public string Description { get; } = description;

    [ObservableProperty] private string _statusText = Resources.Strings.Mode_Checking;
    [ObservableProperty] private string _buttonText = Resources.Strings.Mode_Btn_Install;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActionButton))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActionEnabled))]
    private bool _isBusy;

    /// <summary>Always show the action button so disabled modes remain visible but greyed out.</summary>
    public bool ShowActionButton => true;

    /// <summary>IceBreaker is currently disabled while under development.</summary>
    public bool CanSelect => Mode != UnlockerMode.IceBreaker;

    /// <summary>Action button is enabled only when selectable and view is idle.</summary>
    public bool IsActionEnabled => CanSelect && !IsBusy;

    /// <summary>Visual opacity to grey out disabled cards.</summary>
    public double CardOpacity => CanSelect ? 1.0 : 0.65;

    /// <summary>OST is the recommended mode.</summary>
    public bool IsRecommended => Mode == UnlockerMode.Ost;

    /// <summary>OST is upstream stable.</summary>
    public bool IsExperimental => false;
}

/// <summary>
/// "Mode" page: OpenSteamTools / BetterSteamTools / Custom. Mutually exclusive, one active at a time.
/// Checks status on page open; each card installs/switches after a Steam-shutdown confirmation, then
/// relaunches Steam so the new mode takes effect.
/// </summary>
public partial class ModeViewModel : ObservableObject
{
    private readonly UnlockerService _unlocker;
    private readonly ToastService _toast;
    private readonly SteamService _steam;

    public ObservableCollection<ModeCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        foreach (var card in Cards)
            card.IsBusy = value;
    }

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    // ── Steam-shutdown confirmation overlay ──────────────────────────
    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private string _confirmTitle = "";
    private ModeCardViewModel? _pendingCard;

    public ModeViewModel(UnlockerService unlocker, ToastService toast, SteamService steam)
    {
        _unlocker = unlocker;
        _toast = toast;
        _steam = steam;
    }

    /// <summary>True if a hidden mode should be shown: its reveal-file exists, or it's the active mode.</summary>
    private bool IsModeVisible(ModeDefinition def)
    {
        if (def.HiddenUnlessFile is null) return true;          // always-visible mode
        if (_unlocker.SelectedMode == def.Mode) return true;    // active → keep visible even if file gone
        string? root = _steam.EffectivePath;
        return root is not null && File.Exists(Path.Combine(root, def.HiddenUnlessFile));
    }

    /// <summary>Rebuild the visible card list (hidden modes appear only when revealed). Preserves
    /// existing cards so their bound state isn't reset; adds/removes as visibility changes.</summary>
    private void SyncCards()
    {
        var visible = _unlocker.Modes
            .Where(IsModeVisible)
            .ToList();

        // Remove cards no longer visible.
        for (int i = Cards.Count - 1; i >= 0; i--)
            if (!visible.Any(d => d.Mode == Cards[i].Mode))
                Cards.RemoveAt(i);

        // Add newly-visible cards in definition order.
        foreach (var def in visible)
            if (!Cards.Any(c => c.Mode == def.Mode))
            {
                int idx = visible.IndexOf(def);
                idx = Math.Min(idx, Cards.Count);
                Cards.Insert(idx, new ModeCardViewModel(def.Mode, def.DisplayName, def.Description) { IsBusy = IsBusy });
            }
    }

    /// <summary>
    /// Page open / refresh. Immediately loads on-disk status (<1ms), then queries GitHub in the background.
    /// </summary>
    private bool _detectionAttempted;

    public async Task LoadAsync(bool forceRefresh = false)
    {
        // First time with no mode selected: try to auto-detect an existing install by hashing the
        // on-disk DLLs against published releases, and adopt the match as active.
        if (!_detectionAttempted && _unlocker.SelectedMode is null)
        {
            _detectionAttempted = true;
            await _unlocker.DetectActiveModeAsync();
        }

        // 1. Instant local file state (<1ms)
        SyncCards();
        var active = _unlocker.SelectedMode;
        foreach (var card in Cards)
        {
            var localState = _unlocker.GetLocalState(card.Mode);
            Apply(card, localState);
        }

        // 2. Background update check
        _ = Task.Run(async () =>
        {
            foreach (var card in Cards)
            {
                if (card.Mode == active)
                {
                    var state = await _unlocker.GetStateAsync(card.Mode, forceRefresh);
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() => Apply(card, state));
                }
            }
        });
    }

    private DateTime _lastCheck;

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _lastCheck = DateTime.UtcNow;
            await LoadAsync(forceRefresh: true);
            _toast.Show(Resources.Strings.Mode_Title, Resources.Strings.Plugin_Toast_UpToDate);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(ModeCardViewModel card, ModeState s)
    {
        if (card.Mode == UnlockerMode.IceBreaker)
        {
            card.StatusText = "Disabled (Work in progress)";
            card.ButtonText = "Disabled";
            card.IsActive = false;
            return;
        }

        if (s.Status == ModeStatus.Unknown)
            card.StatusText = Resources.Strings.Mode_StatusUnavailable;
        else if (!s.IsActive)
            card.StatusText = Resources.Strings.Mode_NotActive;
        else
            card.StatusText = s.Status switch
            {
                ModeStatus.NotInstalled => Resources.Strings.Mode_NotInstalled,
                ModeStatus.UpToDate => Resources.Strings.Mode_UpToDate,
                ModeStatus.UpdateAvailable => Resources.Strings.Mode_UpdateAvailable,
                _ => Resources.Strings.Mode_StatusUnavailable,
            };

        card.ButtonText = (s.IsActive, s.Status) switch
        {
            (true, ModeStatus.UpToDate) => Resources.Strings.Mode_Btn_Reinstall,
            (true, ModeStatus.UpdateAvailable) => Resources.Strings.Mode_Btn_Update,
            (true, _) => Resources.Strings.Mode_Btn_Install,
            (false, _) => Resources.Strings.Mode_Btn_Switch,
        };
        card.IsActive = s.IsActive;
    }

    // ── Install with confirmation ────────────────────────────────────

    /// <summary>Card button → ask the user to confirm (Steam will be closed) before doing anything.</summary>
    [RelayCommand]
    private void Install(ModeCardViewModel card)
    {
        if (IsBusy || card.Mode == UnlockerMode.IceBreaker) return;
        _pendingCard = card;
        ConfirmTitle = card.IsActive
            ? string.Format(Resources.Strings.Mode_Confirm_Reinstall, card.Title)
            : string.Format(Resources.Strings.Mode_Confirm_Switch, card.Title);
        IsConfirming = true;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirming = false;
        _pendingCard = null;
    }

    [RelayCommand]
    private async Task ConfirmInstall()
    {
        IsConfirming = false;
        var card = _pendingCard;
        _pendingCard = null;
        if (card is null) return;

        await RunInstall(card.Mode);
    }

    private async Task RunInstall(UnlockerMode mode)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        bool wasRunning = false;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            wasRunning = SteamService.IsSteamRunning();
            if (wasRunning)
            {
                await Task.Run(_steam.StopSteam);
            }

            var result = await _unlocker.InstallAsync(mode, prog);

            if (result.Success)
            {
                bool started = wasRunning && await Task.Run(_steam.StartSteam);
                _toast.Show(Resources.Strings.Mode_Toast_Updated, started
                    ? string.Format(Resources.Strings.Mode_Toast_Updated_Restarting, mode)
                    : string.Format(Resources.Strings.Mode_Toast_Updated_Start, mode));
            }
            else
            {
                if (wasRunning)
                {
                    await Task.Run(_steam.StartSteam);
                }
                _toast.Show(Resources.Strings.Mode_Toast_InstallFailed, result.Error ?? Resources.Strings.Mode_Toast_InstallFailed_Body, error: true);
            }

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }
}
