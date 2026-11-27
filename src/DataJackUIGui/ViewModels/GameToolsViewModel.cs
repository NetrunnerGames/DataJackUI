using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataJackUIGui.Resources;
using DataJackUIGui.Services;
using DataJackUIGui.Services.AppInfo;
using DataJackUIGui.Services.GameData;

namespace DataJackUIGui.ViewModels;

public partial class GameToolsViewModel : ObservableObject
{
    private readonly SteamService _steam;
    private readonly SteamAppListCache _appList;
    private readonly GameStatsService _stats;
    private readonly PlaytimeService _playtime;
    private readonly AchievementIconCache _icons;
    private readonly ToastService _toast;
    private readonly Dictionary<long, string?> _schemaFetch = new();
    private CancellationTokenSource? _iconLoad;
    private int? _originalMinutes;
    private readonly LaunchModStore _launchMods;

    [ObservableProperty]
    private bool _hasLaunchOverrides;

    [ObservableProperty]
    private LuaTileViewModel? _activeGame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsRestartNote), nameof(PlaytimeRestartNote))]
    private bool _steamRunning;

    [ObservableProperty]
    private string? _achievementsMessage;

    [ObservableProperty]
    private string? _statsMessage;

    [ObservableProperty]
    private bool _showsSets;

    [ObservableProperty]
    private string _achievementSearchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsRestartNote))]
    private bool _cloudRedirectManaged;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveStatsCommand), nameof(UnlockAllCommand), nameof(LockAllCommand))]
    [NotifyPropertyChangedFor(nameof(StatsRestartNote))]
    private bool _isSaving;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePlaytimeCommand))]
    private string _playtimeHours = "";

    [ObservableProperty]
    private string? _playtimeMessage;

    public Action<long, string>? OpenLaunchOptions { get; set; }
    public Action? OpenAllLaunchOverrides { get; set; }

    public ObservableCollection<LaunchEntryViewModel> LaunchOverrides { get; } = new();
    public ObservableCollection<AchievementRowViewModel> Achievements { get; } = new();
    public ObservableCollection<StatRowViewModel> Stats { get; } = new();

    public string? StatsRestartNote
    {
        get
        {
            if (IsSaving) return null;
            if (SteamRunning && CloudRedirectManaged)
            {
                return string.Format(Strings.GameTools_RestartRequired, Strings.GameTools_RestartReason_CloudRedirect);
            }
            return null;
        }
    }

    public bool HasAchievements => Achievements.Count > 0;

    public bool HasNoAchievementMatches
    {
        get
        {
            if (HasAchievements && !string.IsNullOrWhiteSpace(AchievementSearchText))
            {
                return CollectionViewSource.GetDefaultView(Achievements).IsEmpty;
            }
            return false;
        }
    }

    public bool HasStatChanges => Achievements.Any(a => a.IsDirty) || Stats.Any(s => s.IsDirty);

    public bool PlaytimeDirty
    {
        get
        {
            int? num = ParseHours(PlaytimeHours);
            if (num.HasValue && _originalMinutes.HasValue)
            {
                return num.Value != _originalMinutes.Value;
            }
            return false;
        }
    }

    public string? PlaytimeRestartNote
    {
        get
        {
            if (SteamRunning && PlaytimeDirty)
            {
                return string.Format(Strings.GameTools_RestartRequired, Strings.GameTools_RestartReason_Playtime);
            }
            return null;
        }
    }

    public GameToolsViewModel(SteamService steam, SteamAppListCache appList, GameStatsService stats,
        PlaytimeService playtime, AchievementIconCache icons, ToastService toast, LaunchModStore launchMods)
    {
        _steam = steam;
        _appList = appList;
        _stats = stats;
        _playtime = playtime;
        _icons = icons;
        _toast = toast;
        _launchMods = launchMods;

        var defaultView = CollectionViewSource.GetDefaultView(Achievements);
        defaultView.Filter = (obj) =>
        {
            if (obj is not AchievementRowViewModel row) return false;
            string q = AchievementSearchText.Trim();
            if (q.Length == 0) return true;
            if (row.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                row.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                return true;
            return row.ApiName.Contains(q, StringComparison.OrdinalIgnoreCase);
        };
    }

    private void RefreshLaunchOverrides()
    {
        LaunchOverrides.Clear();
        if (ActiveGame != null && _launchMods.Get((int)ActiveGame.AppId) is { } mod)
        {
            foreach (var o in mod.Desired)
                LaunchOverrides.Add(new LaunchEntryViewModel(o));
        }
        HasLaunchOverrides = LaunchOverrides.Count > 0;
    }

    [RelayCommand]
    private void EditLaunchOptions()
    {
        if (ActiveGame != null)
        {
            OpenLaunchOptions?.Invoke(ActiveGame.AppId, ActiveGame.Name);
            RefreshLaunchOverrides();
        }
    }

    [RelayCommand]
    private void ShowAllLaunchOverrides()
    {
        OpenAllLaunchOverrides?.Invoke();
        RefreshLaunchOverrides();
    }

    public void AllowSchemaRefetch(long appId) => _schemaFetch.Remove(appId);

    private async Task LoadGameAsync(LuaTileViewModel game)
    {
        SteamRunning = SteamService.IsSteamRunning();
        var snap = await Task.Run(() => _stats.Load(game.AppId));
        int? minutes = await Task.Run(() => _playtime.GetMinutes(game.AppId));
        _originalMinutes = minutes;
        PlaytimeHours = minutes.HasValue ? (minutes.Value / 60.0).ToString("0.#", CultureInfo.CurrentCulture) : "";
        PlaytimeMessage = minutes.HasValue ? null : Strings.GameTools_Playtime_None;
        OnPropertyChanged(nameof(PlaytimeRestartNote));

        RefreshLaunchOverrides();

        if (snap.HasSchema)
        {
            ShowStats(snap);
            return;
        }

        ClearRows();
        OnPropertyChanged(nameof(HasAchievements));
        OnPropertyChanged(nameof(HasNoAchievementMatches));

        if (!snap.AccountId.HasValue)
        {
            AchievementsMessage = Strings.GameTools_NoAccount;
            StatsMessage = Strings.GameTools_NoAccount;
            return;
        }

        if (_schemaFetch.TryGetValue(game.AppId, out string? cachedErr))
        {
            string msg = cachedErr ?? Strings.GameTools_NoSchema;
            AchievementsMessage = msg;
            StatsMessage = msg;
            return;
        }

        AchievementsMessage = Strings.GameTools_FetchingSchema;
        StatsMessage = Strings.GameTools_FetchingSchema;

        var fetchRes = await _stats.FetchSchemaAsync(game.AppId);
        _schemaFetch[game.AppId] = fetchRes.Error;

        if (ActiveGame?.AppId == game.AppId)
        {
            var snap2 = await Task.Run(() => _stats.Load(game.AppId));
            if (snap2.HasSchema)
            {
                ShowStats(snap2);
                return;
            }
            string finalMsg = fetchRes.Error ?? Strings.GameTools_NoSchema;
            AchievementsMessage = finalMsg;
            StatsMessage = finalMsg;
        }
    }

    private void ClearRows()
    {
        foreach (var a in Achievements) a.PropertyChanged -= OnRowChanged;
        foreach (var s in Stats) s.PropertyChanged -= OnRowChanged;
        Achievements.Clear();
        Stats.Clear();
    }

    private void ShowStats(GameStatsSnapshot snap)
    {
        CloudRedirectManaged = snap.CloudRedirectManaged;
        ClearRows();

        var sets = new Dictionary<string, AchievementSetViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var ach in snap.Achievements)
        {
            string title = SetTitle(snap, ach.GroupId ?? 0);
            if (!sets.TryGetValue(title, out var set))
            {
                set = new AchievementSetViewModel(title);
                sets[title] = set;
            }
            var row = new AchievementRowViewModel(ach, set);
            row.PropertyChanged += OnRowChanged;
            set.Rows.Add(row);
            Achievements.Add(row);
        }

        ShowsSets = sets.Count > 1;

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Achievements);
        view.GroupDescriptions.Clear();
        if (ShowsSets)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AchievementRowViewModel.Set)));
        }

        foreach (var stat in snap.Stats)
        {
            var sRow = new StatRowViewModel(stat);
            sRow.PropertyChanged += OnRowChanged;
            Stats.Add(sRow);
        }

        OnPropertyChanged(nameof(HasAchievements));
        OnPropertyChanged(nameof(HasNoAchievementMatches));
        RowsChanged();

        AchievementsMessage = Achievements.Count == 0 ? Strings.GameTools_NoAchievements : null;
        StatsMessage = Stats.Count == 0 ? Strings.GameTools_NoStats : null;

        _iconLoad?.Cancel();
        _iconLoad = new CancellationTokenSource();
        _ = LoadIconsAsync(Achievements.ToList());
    }

    private async Task LoadIconsAsync(IReadOnlyList<AchievementRowViewModel> rows)
    {
        var ct = _iconLoad?.Token ?? CancellationToken.None;
        foreach (var r in rows)
        {
            if (ct.IsCancellationRequested) return;
            string? c = await _icons.GetAsync(r.Info.IconUrl, ct);
            string? g = await _icons.GetAsync(r.Info.IconGrayUrl, ct);
            if (!ct.IsCancellationRequested)
            {
                r.ColorIcon = c;
                r.GrayIcon = g;
            }
        }
    }

    private string SetTitle(GameStatsSnapshot snap, int groupId)
    {
        var g = snap.Groups.FirstOrDefault(x => x.Id == groupId);
        return g != null && !string.IsNullOrWhiteSpace(g.Name) ? g.Name : Strings.GameTools_Set_Base;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AchievementRowViewModel.Unlocked) ||
            e.PropertyName == nameof(StatRowViewModel.Text) ||
            e.PropertyName == nameof(StatRowViewModel.IsValid))
        {
            RowsChanged();
            if (sender is AchievementRowViewModel r)
                r.Set.NotifyProgressChanged();
        }
    }

    private void RowsChanged()
    {
        SaveStatsCommand.NotifyCanExecuteChanged();
        RevertStatsCommand.NotifyCanExecuteChanged();
        UnlockAllCommand.NotifyCanExecuteChanged();
        LockAllCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasStatChanges));
    }

    private bool CanBulk() => !IsSaving && BulkTargets().Any();

    [RelayCommand(CanExecute = nameof(CanBulk))]
    private void UnlockAll()
    {
        foreach (var r in BulkTargets()) r.Unlocked = true;
    }

    [RelayCommand(CanExecute = nameof(CanBulk))]
    private void LockAll()
    {
        foreach (var r in BulkTargets()) r.Unlocked = false;
    }

    private IEnumerable<AchievementRowViewModel> BulkTargets()
    {
        var defaultView = CollectionViewSource.GetDefaultView(Achievements);
        return defaultView != null ? defaultView.Cast<AchievementRowViewModel>() : Achievements;
    }

    private bool CanSaveStats() => !IsSaving && HasStatChanges && Stats.All(s => s.IsValid);

    [RelayCommand(CanExecute = nameof(CanSaveStats))]
    private async Task SaveStatsAsync()
    {
        if (ActiveGame == null) return;
        var edit = new GameStatsEdit(
            Achievements.Where(a => a.IsDirty).ToDictionary(a => a.ApiName, a => a.Unlocked),
            Stats.Where(s => s.IsDirty && s.Parsed.HasValue).ToDictionary(s => s.ApiName, s => s.Parsed!.Value)
        );
        IsSaving = true;
        try
        {
            var result = await _stats.SaveAsync(ActiveGame.AppId, edit);
            HandleSave(ActiveGame, edit, result);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanRevertStats() => !IsSaving && HasStatChanges;

    [RelayCommand(CanExecute = nameof(CanRevertStats))]
    private Task RevertStatsAsync()
    {
        foreach (var a in Achievements) a.Reset();
        foreach (var s in Stats) s.Reset();
        RowsChanged();
        return Task.CompletedTask;
    }

    private void HandleSave(LuaTileViewModel game, GameStatsEdit edit, StatsSaveResult r)
    {
        switch (r.Outcome)
        {
            case StatsSaveOutcome.SavedLive:
                _toast.Show(Strings.GameTools_Tab_Achievements, Strings.GameTools_Saved_Live);
                Accept(edit);
                break;
            case StatsSaveOutcome.LiveRejected:
                _toast.Show(Strings.GameTools_Tab_Achievements, string.Format(Strings.GameTools_LiveRejected, r.Error), error: true);
                break;
            case StatsSaveOutcome.SavedToFile:
                _toast.Show(Strings.GameTools_Tab_Achievements, Strings.GameTools_Saved_File);
                Accept(edit);
                break;
            case StatsSaveOutcome.Failed:
            default:
                _toast.Show(Strings.GameTools_Tab_Achievements, string.Format(Strings.GameTools_SaveFailed, r.Error), error: true);
                break;
        }

        void Accept(GameStatsEdit e)
        {
            foreach (var a in Achievements)
                if (e.Achievements.ContainsKey(a.ApiName)) a.AcceptChanges();
            foreach (var s in Stats)
                if (e.Stats.ContainsKey(s.ApiName)) s.AcceptChanges();
            RowsChanged();
        }
    }

    private static int? ParseHours(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double h) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out h))
        {
            if (h < 0.0) return null;
            return (int)Math.Round(h * 60.0);
        }
        return null;
    }

    private bool CanSavePlaytime() => PlaytimeDirty && ParseHours(PlaytimeHours).HasValue;

    [RelayCommand(CanExecute = nameof(CanSavePlaytime))]
    private async Task SavePlaytimeAsync()
    {
        if (ActiveGame == null) return;
        int? mins = ParseHours(PlaytimeHours);
        if (!mins.HasValue) return;

        string? err = await Task.Run(() => _playtime.SetMinutes(ActiveGame.AppId, mins.Value));
        if (err != null)
        {
            _toast.Show(Strings.GameTools_Tab_Playtime, err, error: true);
        }
        else
        {
            _originalMinutes = mins;
            _toast.Show(Strings.GameTools_Tab_Playtime, Strings.GameTools_Saved_File);
            OnPropertyChanged(nameof(PlaytimeDirty));
            OnPropertyChanged(nameof(PlaytimeRestartNote));
            SavePlaytimeCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnActiveGameChanged(LuaTileViewModel? oldValue, LuaTileViewModel? newValue)
    {
        _iconLoad?.Cancel();
        if (newValue != null)
        {
            _ = LoadGameAsync(newValue);
        }
        else
        {
            ClearRows();
            LaunchOverrides.Clear();
            HasLaunchOverrides = false;
            PlaytimeHours = "";
            _originalMinutes = null;
            RowsChanged();
        }
    }

    partial void OnAchievementSearchTextChanged(string value)
    {
        CollectionViewSource.GetDefaultView(Achievements)?.Refresh();
        OnPropertyChanged(nameof(HasNoAchievementMatches));
    }

    partial void OnPlaytimeHoursChanged(string value)
    {
        OnPropertyChanged(nameof(PlaytimeDirty));
        OnPropertyChanged(nameof(PlaytimeRestartNote));
        SavePlaytimeCommand.NotifyCanExecuteChanged();
    }
}
