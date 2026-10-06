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

    /// <summary>Both IceBreaker and OST carry native CloudRedirect support.</summary>
    public bool SupportsCloudRedirect => Mode is UnlockerMode.IceBreaker or UnlockerMode.Ost;
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
    private readonly CloudRedirectService _cloudRedirect;

    public ObservableCollection<ModeCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    [NotifyPropertyChangedFor(nameof(CanUseCloudRedirect))]
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

    public ModeViewModel(UnlockerService unlocker, ToastService toast, SteamService steam,
        CloudRedirectService cloudRedirect)
    {
        _unlocker = unlocker;
        _toast = toast;
        _steam = steam;
        _cloudRedirect = cloudRedirect;
    }

    /// <summary>CloudRedirect "Manage" (add-on panel): download (cache) the CloudRedirect GUI and launch it.</summary>
    [RelayCommand]
    private async Task ManageCloudRedirect()
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            bool ok = await _cloudRedirect.LaunchAsync(prog);
            if (!ok)
                _toast.Show(Resources.Strings.Mode_CloudRedirect_Manage,
                    Resources.Strings.Mode_CloudRedirect_LaunchFailed, error: true);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    // ── CloudRedirect add-on (bottom panel; usable when either OST or BST is active) ───
    private const string CloudRedirectTitle = "CloudRedirect"; // product name, not localized

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectManage))]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectUpdate))]
    [NotifyPropertyChangedFor(nameof(CanUseCloudRedirect))]
    private bool _cloudRedirectUnlocked;

    /// <summary>Buttons on the add-on panel are usable only when unlocked (Nightly active) and idle.</summary>
    public bool CanUseCloudRedirect => CloudRedirectUnlocked && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloudRedirectToggleText))]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectManage))]
    private bool _cloudRedirectEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectManage))]
    private bool _cloudRedirectInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloudRedirectUpdate))]
    private bool _cloudRedirectUpdateAvailable;

    [ObservableProperty] private string _cloudRedirectStatusText = "";

    public string CloudRedirectToggleText => CloudRedirectEnabled
        ? Resources.Strings.Mode_CloudRedirect_Disable
        : Resources.Strings.Mode_CloudRedirect_Enable;
    public bool ShowCloudRedirectUpdate => CloudRedirectUnlocked && CloudRedirectUpdateAvailable;
    public bool ShowCloudRedirectManage => CloudRedirectUnlocked && CloudRedirectInstalled && CloudRedirectEnabled;

    /// <summary>Refresh the add-on panel state. Reads dll/toml from disk (cheap); checks GitHub for an
    /// update only when unlocked (Nightly active), respecting forceRefresh.</summary>
    /// <summary>Refresh the add-on panel state locally first, then check GitHub for updates.</summary>
    private void RefreshCloudRedirectLocal()
    {
        CloudRedirectUnlocked = _unlocker.SelectedMode is UnlockerMode.Ost or UnlockerMode.IceBreaker;
        var s = _unlocker.GetCloudRedirectLocalState();
        CloudRedirectInstalled = s.Installed;
        CloudRedirectEnabled = s.Enabled;
        CloudRedirectUpdateAvailable = false;

        CloudRedirectStatusText = !CloudRedirectUnlocked ? Resources.Strings.Mode_CloudRedirect_Locked
            : !s.Installed ? Resources.Strings.Mode_CloudRedirect_Status_NotInstalled
            : s.Enabled ? Resources.Strings.Mode_CloudRedirect_Status_Enabled
            : Resources.Strings.Mode_CloudRedirect_Status_Disabled;
    }

    private async Task RefreshCloudRedirectAsync(bool forceRefresh)
    {
        bool unlocked = _unlocker.SelectedMode is UnlockerMode.Ost or UnlockerMode.IceBreaker;
        if (unlocked && _unlocker.SelectedMode == UnlockerMode.Ost && _steam.EffectivePath is { } root)
        {
            if (!File.Exists(Path.Combine(root, "cloud_redirect.dll")))
            {
                try { await _unlocker.EnableCloudRedirectAsync(); } catch { }
            }
        }
        var s = await _unlocker.GetCloudRedirectStateAsync(checkUpdate: unlocked, forceRefresh);
        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
        {
            CloudRedirectUnlocked = unlocked;
            CloudRedirectInstalled = s.Installed;
            CloudRedirectEnabled = s.Enabled;
            CloudRedirectUpdateAvailable = s.UpdateAvailable;

            CloudRedirectStatusText = !CloudRedirectUnlocked ? Resources.Strings.Mode_CloudRedirect_Locked
                : !s.Installed ? Resources.Strings.Mode_CloudRedirect_Status_NotInstalled
                : s.UpdateAvailable ? Resources.Strings.Mode_CloudRedirect_Status_UpdateAvailable
                : s.Enabled ? Resources.Strings.Mode_CloudRedirect_Status_Enabled
                : Resources.Strings.Mode_CloudRedirect_Status_Disabled;
        });
    }

    /// <summary>Enable/disable the add-on (edits opensteamtool.toml; first enable also downloads the dll).
    /// Lightweight, no Steam close; the change applies on the next Steam launch.</summary>
    [RelayCommand]
    private async Task ToggleCloudRedirect()
    {
        if (IsBusy || !CloudRedirectUnlocked) return;
        bool enabling = !CloudRedirectEnabled;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            var result = enabling
                ? await _unlocker.EnableCloudRedirectAsync(prog)
                : _unlocker.DisableCloudRedirect();

            if (result.Success)
                _toast.Show(CloudRedirectTitle, enabling
                    ? Resources.Strings.Mode_CloudRedirect_Toast_Enabled
                    : Resources.Strings.Mode_CloudRedirect_Toast_Disabled);
            else
                _toast.Show(CloudRedirectTitle, result.Error ?? "", error: true);

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>Replace cloud_redirect.dll with the latest. Reports "close Steam" if it's locked.</summary>
    [RelayCommand]
    private async Task UpdateCloudRedirect()
    {
        if (IsBusy || !CloudRedirectUnlocked) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            var result = await _unlocker.UpdateCloudRedirectAsync(prog);
            if (result.Success)
                _toast.Show(CloudRedirectTitle, Resources.Strings.Mode_CloudRedirect_Toast_Updated);
            else
                _toast.Show(CloudRedirectTitle, result.Error ?? "", error: true);

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
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
        RefreshCloudRedirectLocal();

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
            await RefreshCloudRedirectAsync(forceRefresh);
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
