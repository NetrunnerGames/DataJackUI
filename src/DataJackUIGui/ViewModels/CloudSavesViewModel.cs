using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataJackUIGui.Services;
using Microsoft.Win32;

namespace DataJackUIGui.ViewModels;

public partial class CloudGameItemViewModel : ObservableObject
{
    private readonly GameSaveTarget _target;

    public CloudGameItemViewModel(GameSaveTarget target, string? initialCover = null)
    {
        _target = target;
        AppId = target.AppId;
        GameName = target.GameName;
        SavePath = target.SavePath;
        LocationType = target.LocationType;
        FileCount = target.FileCount;
        TotalSizeBytes = target.TotalSizeBytes;
        LastModified = target.LastModified;
        SnapshotCount = target.SnapshotCount;
        LastBackupDate = target.LastBackupDate;
        _coverPath = initialCover;
    }

    public long AppId { get; }

    [ObservableProperty] private string _gameName;
    [ObservableProperty] private string? _coverPath;
    [ObservableProperty] private string _savePath;
    [ObservableProperty] private SaveLocationType _locationType;
    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private long _totalSizeBytes;
    [ObservableProperty] private DateTime? _lastModified;
    [ObservableProperty] private int _snapshotCount;
    [ObservableProperty] private DateTime? _lastBackupDate;
    [ObservableProperty] private bool _isExpanded;

    public ObservableCollection<SaveBackupSnapshot> Snapshots { get; } = [];

    public bool HasBackups => SnapshotCount > 0;
    public bool HasSavePath => !string.IsNullOrWhiteSpace(SavePath) && Directory.Exists(SavePath);
    public string FormattedSize => CloudSavesService.FormatBytes(TotalSizeBytes);

    public string LocationDisplay => LocationType switch
    {
        SaveLocationType.SteamUserData => "Steam UserData",
        SaveLocationType.Documents => "Documents / My Games",
        SaveLocationType.SavedGames => "Saved Games",
        SaveLocationType.AppDataLocal => "Local AppData",
        SaveLocationType.AppDataRoaming => "Roaming AppData",
        SaveLocationType.InstallDir => "Game Directory",
        SaveLocationType.Custom => "Custom Location",
        _ => "Local Storage"
    };

    public void UpdateStats(string path, SaveLocationType type, int count, long bytes, DateTime? lastMod)
    {
        SavePath = path;
        LocationType = type;
        FileCount = count;
        TotalSizeBytes = bytes;
        LastModified = lastMod;
        OnPropertyChanged(nameof(FormattedSize));
        OnPropertyChanged(nameof(HasSavePath));
        OnPropertyChanged(nameof(LocationDisplay));
    }

    public void UpdateSnapshotCount(int count, DateTime? lastDate)
    {
        SnapshotCount = count;
        LastBackupDate = lastDate;
        OnPropertyChanged(nameof(HasBackups));
    }

    public void RefreshSnapshots(CloudSavesService svc)
    {
        Snapshots.Clear();
        var list = svc.GetSnapshots(AppId);
        foreach (var s in list)
        {
            Snapshots.Add(s);
        }
        DateTime? lastDate = list.Count > 0 ? list[0].Timestamp : null;
        UpdateSnapshotCount(list.Count, lastDate);
    }
}

public partial class CloudSavesViewModel : ObservableObject
{
    private readonly CloudSavesService _cloudSaves;
    private readonly SettingsService _settings;
    private readonly SteamAppInfoCache _appInfo;
    private readonly CoverCache _covers;
    private readonly ToastService _toast;

    private readonly List<CloudGameItemViewModel> _allGames = [];

    public CloudSavesViewModel(
        CloudSavesService cloudSaves,
        SettingsService settings,
        SteamAppInfoCache appInfo,
        CoverCache covers,
        ToastService toast)
    {
        _cloudSaves = cloudSaves;
        _settings = settings;
        _appInfo = appInfo;
        _covers = covers;
        _toast = toast;

        UpdateProviderDetection();
    }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _searchText = "";

    [ObservableProperty] private CloudGameItemViewModel? _selectedGame;
    public ObservableCollection<CloudGameItemViewModel> FilteredGames { get; } = [];
    public ObservableCollection<SaveBackupSnapshot> Snapshots { get; } = [];

    [ObservableProperty] private string _oneDriveStatus = "Checking…";
    [ObservableProperty] private bool _isOneDriveConnected;
    [ObservableProperty] private string? _oneDriveAccount;
    [ObservableProperty] private string _googleDriveStatus = "Checking…";
    [ObservableProperty] private bool _isGoogleDriveConnected;
    [ObservableProperty] private string? _googleDriveAccount;
    [ObservableProperty] private string _rcloneStatus = "Checking…";
    [ObservableProperty] private bool _isRcloneDetected;

    public string[] AvailableProviders => ["OneDrive", "GoogleDrive", "Rclone", "Local"];

    public string SelectedProvider
    {
        get => _settings.CloudSavesProvider;
        set
        {
            if (_settings.CloudSavesProvider != value)
            {
                _settings.CloudSavesProvider = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsRcloneProvider));
                OnPropertyChanged(nameof(IsLocalProvider));
                OnPropertyChanged(nameof(IsGoogleDriveProvider));
                OnPropertyChanged(nameof(IsOneDriveProvider));
            }
        }
    }

    public bool IsOneDriveProvider => string.Equals(SelectedProvider, "OneDrive", StringComparison.OrdinalIgnoreCase);
    public bool IsGoogleDriveProvider => string.Equals(SelectedProvider, "GoogleDrive", StringComparison.OrdinalIgnoreCase);
    public bool IsRcloneProvider => string.Equals(SelectedProvider, "Rclone", StringComparison.OrdinalIgnoreCase);
    public bool IsLocalProvider => string.Equals(SelectedProvider, "Local", StringComparison.OrdinalIgnoreCase);

    public string RcloneRemote
    {
        get => _settings.CloudSavesRcloneRemote;
        set { _settings.CloudSavesRcloneRemote = value; OnPropertyChanged(); }
    }

    public string LocalBackupPath
    {
        get => _settings.CloudSavesLocalPath ?? "";
        set
        {
            string trimmed = (value ?? "").Trim();
            string? normalized = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
            if (_settings.CloudSavesLocalPath != normalized)
            {
                _settings.CloudSavesLocalPath = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCustomLocalBackupPath));
                OnPropertyChanged(nameof(EffectiveLocalBackupPath));
                _ = ScanGamesAsync();
            }
        }
    }

    public bool HasCustomLocalBackupPath => !string.IsNullOrWhiteSpace(_settings.CloudSavesLocalPath);

    public string EffectiveLocalBackupPath => _cloudSaves.BaseBackupDir;

    public string RcloneExePath
    {
        get => _settings.CloudSavesRcloneExePath ?? "";
        set
        {
            _settings.CloudSavesRcloneExePath = value;
            OnPropertyChanged();
            UpdateProviderDetection();
        }
    }

    public bool AutoSync
    {
        get => _settings.CloudSavesAutoSync;
        set { _settings.CloudSavesAutoSync = value; OnPropertyChanged(); }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedGameChanged(CloudGameItemViewModel? value)
    {
        LoadSnapshotsForSelected();
        if (value != null)
        {
            _ = ResolveGameDetailsAsync(value);
        }
    }

    public async Task InitializeAsync()
    {
        if (_allGames.Count == 0)
        {
            await ScanGamesAsync();
        }
    }

    [RelayCommand]
    public async Task ScanGamesAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusMessage = "Scanning for game saves…";

        try
        {
            var rawTargets = await Task.Run(() => _cloudSaves.EnumerateGameSaves());

            _allGames.Clear();
            foreach (var t in rawTargets)
            {
                var vm = new CloudGameItemViewModel(t, _covers.GetLocalPath(t.AppId));
                vm.RefreshSnapshots(_cloudSaves);
                _allGames.Add(vm);
            }

            ApplyFilter();
            UpdateProviderDetection();

            if (SelectedGame == null && FilteredGames.Count > 0)
            {
                SelectedGame = FilteredGames[0];
            }
            else if (SelectedGame != null)
            {
                // Re-select matching instance
                SelectedGame = FilteredGames.FirstOrDefault(g => g.AppId == SelectedGame.AppId) ?? FilteredGames.FirstOrDefault();
            }

            StatusMessage = $"Found {_allGames.Count} game(s) with save data or snapshots.";

            // Resolve game names and covers in background
            _ = ResolveAllCoversAndNamesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        FilteredGames.Clear();
        string q = SearchText.Trim();

        foreach (var g in _allGames)
        {
            if (string.IsNullOrWhiteSpace(q) ||
                g.GameName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                g.AppId.ToString().Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredGames.Add(g);
            }
        }
        OnPropertyChanged(nameof(HasFilteredGames));
    }

    public bool HasFilteredGames => FilteredGames.Count > 0;

    private async Task ResolveAllCoversAndNamesAsync()
    {
        var games = _allGames.ToList();
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 6 };
        try
        {
            await Parallel.ForEachAsync(games, parallelOptions, async (g, ct) =>
            {
                await ResolveGameDetailsAsync(g);
            });
        }
        catch { }
    }

    private async Task ResolveGameDetailsAsync(CloudGameItemViewModel g)
    {
        try
        {
            // 1. Fast cover resolution (local path or Steam CDN guess)
            if (string.IsNullOrEmpty(g.CoverPath))
            {
                string? local = _covers.GetLocalPath(g.AppId);
                if (local != null)
                {
                    g.CoverPath = local;
                }
                else
                {
                    string guess = SteamAppInfoCache.GuessHeaderImageUrl(g.AppId);
                    local = await _covers.EnsureAsync(g.AppId, guess);
                    if (!string.IsNullOrEmpty(local))
                    {
                        g.CoverPath = local;
                    }
                }
            }

            // 2. Resolve title if it's currently just numerical App ID
            if (g.GameName == g.AppId.ToString())
            {
                var info = _appInfo.GetCached(g.AppId) ?? await _appInfo.ResolveAsync(g.AppId);
                if (info != null && !string.IsNullOrWhiteSpace(info.Name))
                {
                    g.GameName = info.Name;
                }

                if (string.IsNullOrEmpty(g.CoverPath) && !string.IsNullOrEmpty(info?.HeaderImage))
                {
                    string? cover = await _covers.EnsureAsync(g.AppId, info.HeaderImage);
                    if (!string.IsNullOrEmpty(cover)) g.CoverPath = cover;
                }
            }
        }
        catch { }
    }

    public void LoadSnapshotsForSelected()
    {
        Snapshots.Clear();
        if (SelectedGame == null) return;

        var list = _cloudSaves.GetSnapshots(SelectedGame.AppId);
        foreach (var s in list)
        {
            Snapshots.Add(s);
        }

        DateTime? lastDate = list.Count > 0 ? list[0].Timestamp : null;
        SelectedGame.UpdateSnapshotCount(list.Count, lastDate);
    }

    [RelayCommand]
    public async Task BackupSelectedAsync()
    {
        if (SelectedGame == null) return;
        if (string.IsNullOrWhiteSpace(SelectedGame.SavePath) || !Directory.Exists(SelectedGame.SavePath))
        {
            _toast.Show("Cloud Saves", "No valid save path detected for this game.", error: true);
            return;
        }

        IsBusy = true;
        StatusMessage = $"Creating backup snapshot for {SelectedGame.GameName}…";

        try
        {
            var snap = await Task.Run(() =>
                _cloudSaves.CreateBackupSnapshot(SelectedGame.AppId, SelectedGame.GameName, SelectedGame.SavePath));

            LoadSnapshotsForSelected();
            _toast.Show("Cloud Saves", $"Backup created: {snap.SnapshotId} ({snap.FormattedSize})");
            StatusMessage = $"Backup snapshot {snap.SnapshotId} created successfully.";
        }
        catch (Exception ex)
        {
            _toast.Show("Backup Failed", ex.Message, error: true);
            StatusMessage = $"Backup failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task BackupAllAsync()
    {
        var targets = _allGames.Where(g => g.HasSavePath).ToList();
        if (targets.Count == 0)
        {
            _toast.Show("Cloud Saves", "No games with active save folders found.", error: true);
            return;
        }

        IsBusy = true;
        StatusMessage = $"Backing up {targets.Count} game(s)…";
        int succeeded = 0;

        try
        {
            await Task.Run(() =>
            {
                foreach (var g in targets)
                {
                    try
                    {
                        _cloudSaves.CreateBackupSnapshot(g.AppId, g.GameName, g.SavePath);
                        g.RefreshSnapshots(_cloudSaves);
                        succeeded++;
                    }
                    catch { }
                }
            });

            LoadSnapshotsForSelected();
            _toast.Show("Cloud Saves", $"Backed up {succeeded} game(s) successfully!");
            StatusMessage = $"Backed up {succeeded} of {targets.Count} game(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RestoreSnapshotAsync(SaveBackupSnapshot? snapshot)
    {
        if (snapshot == null || SelectedGame == null) return;

        IsBusy = true;
        StatusMessage = $"Restoring snapshot {snapshot.SnapshotId}…";

        try
        {
            string target = SelectedGame.HasSavePath ? SelectedGame.SavePath : snapshot.SourcePath;
            if (string.IsNullOrWhiteSpace(target))
            {
                _toast.Show("Restore Failed", "Could not determine target save directory.", error: true);
                return;
            }

            await Task.Run(() => _cloudSaves.RestoreSnapshot(snapshot, target));

            // Reload snapshots because a pre-restore safety snapshot was generated
            LoadSnapshotsForSelected();

            // Refresh directory stats
            var s = CloudSavesService.GetDirectoryStats(target);
            SelectedGame.UpdateStats(target, SelectedGame.LocationType, s.Count, s.Bytes, s.LastMod);

            _toast.Show("Cloud Saves", $"Restored snapshot {snapshot.SnapshotId} (Safety backup saved).");
            StatusMessage = $"Restored snapshot {snapshot.SnapshotId} successfully.";
        }
        catch (Exception ex)
        {
            _toast.Show("Restore Failed", ex.Message, error: true);
            StatusMessage = $"Restore failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void DeleteSnapshot(SaveBackupSnapshot? snapshot)
    {
        if (snapshot == null || SelectedGame == null) return;

        try
        {
            _cloudSaves.DeleteSnapshot(snapshot);
            LoadSnapshotsForSelected();
            _toast.Show("Cloud Saves", $"Deleted snapshot {snapshot.SnapshotId}.");
        }
        catch (Exception ex)
        {
            _toast.Show("Delete Failed", ex.Message, error: true);
        }
    }

    [RelayCommand]
    public void BrowseCustomSaveLocation()
    {
        if (SelectedGame == null) return;

        var dialog = new OpenFolderDialog
        {
            Title = $"Select Save Folder for {SelectedGame.GameName}",
            InitialDirectory = SelectedGame.HasSavePath ? SelectedGame.SavePath : null
        };

        if (dialog.ShowDialog() == true)
        {
            string chosen = dialog.FolderName;
            _settings.SetCustomSavePath(SelectedGame.AppId, chosen);
            var stats = CloudSavesService.GetDirectoryStats(chosen);
            SelectedGame.UpdateStats(chosen, SaveLocationType.Custom, stats.Count, stats.Bytes, stats.LastMod);
            _toast.Show("Cloud Saves", $"Custom save location set: {chosen}");
        }
    }

    [RelayCommand]
    public void ResetCustomSaveLocation()
    {
        if (SelectedGame == null) return;

        _settings.SetCustomSavePath(SelectedGame.AppId, null);
        var (type, path, count, bytes, lastMod) = _cloudSaves.DetectSavePath(SelectedGame.AppId, SelectedGame.GameName);
        SelectedGame.UpdateStats(path ?? "", type, count, bytes, lastMod);
        _toast.Show("Cloud Saves", "Reset save path to auto-detection.");
    }

    [RelayCommand]
    public void OpenLiveSaveFolder()
    {
        if (SelectedGame == null || string.IsNullOrWhiteSpace(SelectedGame.SavePath)) return;
        SteamService.ShowInExplorer(SelectedGame.SavePath);
    }

    [RelayCommand]
    public void OpenSnapshotsFolder()
    {
        if (SelectedGame == null)
        {
            SteamService.ShowInExplorer(_cloudSaves.BaseBackupDir);
            return;
        }
        string dir = _cloudSaves.GetAppBackupDir(SelectedGame.AppId);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        SteamService.ShowInExplorer(dir);
    }

    [RelayCommand]
    public void ToggleGameExpanded(CloudGameItemViewModel? game)
    {
        if (game == null) return;
        SelectedGame = game;
        game.IsExpanded = !game.IsExpanded;
        if (game.IsExpanded && game.Snapshots.Count == 0)
        {
            game.RefreshSnapshots(_cloudSaves);
        }
    }

    [RelayCommand]
    public async Task BackupGameAsync(CloudGameItemViewModel? game)
    {
        var target = game ?? SelectedGame;
        if (target == null) return;
        SelectedGame = target;

        if (string.IsNullOrWhiteSpace(target.SavePath) || !Directory.Exists(target.SavePath))
        {
            _toast.Show("Cloud Saves", "No valid save path detected for this game.", error: true);
            return;
        }

        IsBusy = true;
        StatusMessage = $"Creating backup snapshot for {target.GameName}…";

        try
        {
            var snap = await Task.Run(() =>
                _cloudSaves.CreateBackupSnapshot(target.AppId, target.GameName, target.SavePath));

            target.RefreshSnapshots(_cloudSaves);
            target.IsExpanded = true;
            LoadSnapshotsForSelected();
            _toast.Show("Cloud Saves", $"Backup created: {snap.SnapshotId} ({snap.FormattedSize})");
            StatusMessage = $"Backup snapshot {snap.SnapshotId} created successfully.";
        }
        catch (Exception ex)
        {
            _toast.Show("Backup Failed", ex.Message, error: true);
            StatusMessage = $"Backup failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RestoreGameSnapshotAsync(SaveBackupSnapshot? snapshot)
    {
        if (snapshot == null) return;
        var game = _allGames.FirstOrDefault(g => g.AppId == snapshot.AppId) ?? SelectedGame;
        if (game != null) SelectedGame = game;

        IsBusy = true;
        StatusMessage = $"Restoring snapshot {snapshot.SnapshotId}…";

        try
        {
            string target = (game != null && game.HasSavePath) ? game.SavePath : snapshot.SourcePath;
            if (string.IsNullOrWhiteSpace(target))
            {
                _toast.Show("Restore Failed", "Could not determine target save directory.", error: true);
                return;
            }

            await Task.Run(() => _cloudSaves.RestoreSnapshot(snapshot, target));

            game?.RefreshSnapshots(_cloudSaves);
            LoadSnapshotsForSelected();

            if (game != null)
            {
                var s = CloudSavesService.GetDirectoryStats(target);
                game.UpdateStats(target, game.LocationType, s.Count, s.Bytes, s.LastMod);
            }

            _toast.Show("Cloud Saves", $"Restored snapshot {snapshot.SnapshotId} (Safety backup saved).");
            StatusMessage = $"Restored snapshot {snapshot.SnapshotId} successfully.";
        }
        catch (Exception ex)
        {
            _toast.Show("Restore Failed", ex.Message, error: true);
            StatusMessage = $"Restore failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void DeleteGameSnapshot(SaveBackupSnapshot? snapshot)
    {
        if (snapshot == null) return;
        var game = _allGames.FirstOrDefault(g => g.AppId == snapshot.AppId) ?? SelectedGame;

        try
        {
            _cloudSaves.DeleteSnapshot(snapshot);
            game?.RefreshSnapshots(_cloudSaves);
            LoadSnapshotsForSelected();
            _toast.Show("Cloud Saves", $"Deleted snapshot {snapshot.SnapshotId}.");
        }
        catch (Exception ex)
        {
            _toast.Show("Delete Failed", ex.Message, error: true);
        }
    }

    [RelayCommand]
    public void OpenGameSaveFolder(CloudGameItemViewModel? game)
    {
        var target = game ?? SelectedGame;
        if (target == null || string.IsNullOrWhiteSpace(target.SavePath)) return;
        SelectedGame = target;
        SteamService.ShowInExplorer(target.SavePath);
    }

    [RelayCommand]
    public void OpenGameSnapshotsFolder(CloudGameItemViewModel? game)
    {
        var target = game ?? SelectedGame;
        if (target == null)
        {
            SteamService.ShowInExplorer(_cloudSaves.BaseBackupDir);
            return;
        }
        SelectedGame = target;
        string dir = _cloudSaves.GetAppBackupDir(target.AppId);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        SteamService.ShowInExplorer(dir);
    }

    [RelayCommand]
    public void BrowseGameCustomSaveLocation(CloudGameItemViewModel? game)
    {
        var target = game ?? SelectedGame;
        if (target == null) return;
        SelectedGame = target;

        var dialog = new OpenFolderDialog
        {
            Title = $"Select Save Folder for {target.GameName}",
            InitialDirectory = target.HasSavePath ? target.SavePath : null
        };

        if (dialog.ShowDialog() == true)
        {
            string chosen = dialog.FolderName;
            _settings.SetCustomSavePath(target.AppId, chosen);
            var stats = CloudSavesService.GetDirectoryStats(chosen);
            target.UpdateStats(chosen, SaveLocationType.Custom, stats.Count, stats.Bytes, stats.LastMod);
            _toast.Show("Cloud Saves", $"Custom save location set: {chosen}");
        }
    }

    [RelayCommand]
    public async Task SyncToCloudAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = $"Syncing backups to {SelectedProvider}…";

        var progress = new Progress<string>(msg => StatusMessage = msg);

        try
        {
            var res = await _cloudSaves.SyncToCloudAsync(progress);
            if (res.Success)
            {
                _toast.Show("Cloud Sync", res.Message);
            }
            else
            {
                _toast.Show("Cloud Sync Error", res.Message, error: true);
            }
            StatusMessage = res.Message;
        }
        catch (Exception ex)
        {
            _toast.Show("Cloud Sync Error", ex.Message, error: true);
            StatusMessage = $"Sync error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SyncFromCloudAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = $"Restoring backups from {SelectedProvider}…";

        var progress = new Progress<string>(msg => StatusMessage = msg);

        try
        {
            var res = await _cloudSaves.SyncFromCloudAsync(progress);
            if (res.Success)
            {
                _toast.Show("Cloud Restore", res.Message);
                await ScanGamesAsync();
            }
            else
            {
                _toast.Show("Cloud Restore Error", res.Message, error: true);
            }
            StatusMessage = res.Message;
        }
        catch (Exception ex)
        {
            _toast.Show("Cloud Restore Error", ex.Message, error: true);
            StatusMessage = $"Restore error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void ConfigureRclone()
    {
        bool ok = _cloudSaves.LaunchRcloneConfig();
        if (ok)
        {
            _toast.Show("Rclone", "Opened rclone interactive configuration in a new window.");
        }
        else
        {
            _toast.Show("Rclone", "Failed to start rclone. Please verify rclone path.", error: true);
        }
    }

    [RelayCommand]
    public void BrowseLocalBackupPath()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Destination Folder for Local Backups",
            InitialDirectory = Directory.Exists(_cloudSaves.BaseBackupDir) ? _cloudSaves.BaseBackupDir : null
        };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            LocalBackupPath = dialog.FolderName;
            _toast.Show("Local Backups", $"Backup location updated: {dialog.FolderName}");
        }
    }

    [RelayCommand]
    public void ResetLocalBackupPath()
    {
        LocalBackupPath = "";
        _toast.Show("Local Backups", "Reset backup directory to default (%APPDATA%\\DataJackUI\\save_backups).");
    }

    [RelayCommand]
    public void OpenLocalBackupFolder()
    {
        string dir = _cloudSaves.BaseBackupDir;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _toast.Show("Open Folder Error", ex.Message, error: true);
        }
    }

    [RelayCommand]
    public void BrowseRcloneExe()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Locate rclone.exe",
            Filter = "rclone executable (rclone.exe)|rclone.exe|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            RcloneExePath = dialog.FileName;
        }
    }

    [RelayCommand]
    public void OpenRcloneWebsite()
    {
        SteamService.OpenUrl("https://rclone.org/downloads/");
    }

    [RelayCommand]
    public async Task ConnectGoogleDriveAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Connecting to Google Drive… Please complete authorization in your browser.";

        try
        {
            var res = await _cloudSaves.ConnectGoogleDriveAsync(msg => StatusMessage = msg);
            if (res.Success)
            {
                _toast.Show("Google Drive", $"Connected successfully as {res.Account ?? "Google User"}.");
                UpdateProviderDetection();
            }
            else
            {
                _toast.Show("Google Drive", res.Message, error: true);
            }
            StatusMessage = res.Message;
        }
        catch (Exception ex)
        {
            _toast.Show("Google Drive Error", ex.Message, error: true);
            StatusMessage = $"Google Drive auth error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void DisconnectGoogleDrive()
    {
        _cloudSaves.DisconnectGoogleDrive();
        UpdateProviderDetection();
        _toast.Show("Google Drive", "Disconnected Google Drive account.");
    }

    [RelayCommand]
    public async Task ConnectOneDriveAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Connecting to OneDrive… Please complete authorization in your browser.";

        try
        {
            var res = await _cloudSaves.ConnectOneDriveAsync(msg => StatusMessage = msg);
            if (res.Success)
            {
                _toast.Show("OneDrive", $"Connected successfully as {res.Account ?? "Microsoft User"}.");
                UpdateProviderDetection();
            }
            else
            {
                _toast.Show("OneDrive", res.Message, error: true);
            }
            StatusMessage = res.Message;
        }
        catch (Exception ex)
        {
            _toast.Show("OneDrive Error", ex.Message, error: true);
            StatusMessage = $"OneDrive auth error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void DisconnectOneDrive()
    {
        _cloudSaves.DisconnectOneDrive();
        UpdateProviderDetection();
        _toast.Show("OneDrive", "Disconnected OneDrive account.");
    }

    public void UpdateProviderDetection()
    {
        IsOneDriveConnected = _cloudSaves.IsOneDriveConnected;
        OneDriveAccount = _cloudSaves.GetOneDriveAccount();
        if (IsOneDriveConnected)
        {
            OneDriveStatus = !string.IsNullOrEmpty(OneDriveAccount) ? $"Connected ({OneDriveAccount})" : "Connected";
        }
        else
        {
            string? oneDrive = _cloudSaves.ResolveOneDriveFolder();
            OneDriveStatus = oneDrive != null ? "Local folder detected" : "Not connected";
        }

        IsGoogleDriveConnected = _cloudSaves.IsGoogleDriveConnected;
        GoogleDriveAccount = _cloudSaves.GetGoogleDriveAccount();
        if (IsGoogleDriveConnected)
        {
            GoogleDriveStatus = !string.IsNullOrEmpty(GoogleDriveAccount) ? $"Connected ({GoogleDriveAccount})" : "Connected";
        }
        else
        {
            string? gdrive = _cloudSaves.ResolveGoogleDriveFolder();
            GoogleDriveStatus = gdrive != null ? "Local folder detected" : "Not connected";
        }

        string? rclone = _cloudSaves.FindRcloneExe();
        IsRcloneDetected = rclone != null;
        RcloneStatus = rclone != null ? "Ready" : "Not found (click to configure)";
    }
}
