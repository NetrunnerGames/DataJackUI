using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataJackUIGui.Services;
using DataJackUIGui.Models;
using Microsoft.Win32;

namespace DataJackUIGui.ViewModels;

/// <summary>A selectable UI language. <see cref="Tag"/> is the BCP-47 tag ("en", "zh-Hans") or null for
/// "follow the system display language".</summary>
public record LanguageOption(string Display, string? Tag);

public record DnsModeOption(string Display, string Tag);

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly SteamService _steam;
    private readonly UpdateService _updateService;
    private readonly UnlockerService? _unlocker;

    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string? _updateStatusText;
    [ObservableProperty] private string _updateStatusColor = "#9ca3af";

    public string CurrentAppVersion => $"v{typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "2.21.0"}";

    // â”€â”€ IceBreaker updater â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    [ObservableProperty] private bool _isCheckingIceBreaker;
    [ObservableProperty] private bool _isUpdatingIceBreaker;
    [ObservableProperty] private double _iceBreakerDownloadProgress;
    [ObservableProperty] private bool _iceBreakerUpdateAvailable;
    [ObservableProperty] private string? _iceBreakerVersionBadge;
    [ObservableProperty] private string? _iceBreakerStatusText;
    [ObservableProperty] private string _iceBreakerStatusColor = "#9ca3af";

    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private string _username = "anonymous";

    partial void OnUsernameChanged(string value)
    {
        _settings.CustomUsername = value;
    }

    [ObservableProperty] private string? _userId;
    [ObservableProperty] private string? _email;
    [ObservableProperty] private string? _avatarUrl;
    [ObservableProperty] private ImageSource? _avatarImage;

    [ObservableProperty] private bool _isAvatarDialogOpen;
    [ObservableProperty] private string _avatarUrlInput = "";
    [ObservableProperty] private bool _isEditingProfile;

    public bool HasCustomAvatar => !string.IsNullOrWhiteSpace(_settings.CustomAvatarPath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRealUser))]
    private bool _isGuest = true;

    public bool IsRealUser => !IsGuest;

    // â”€â”€ Login-required redirect banner â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    /// <summary>Set by App when navigating here from a protected action. Null = banner hidden.</summary>
    [ObservableProperty] private string? _loginRequiredMessage;

    [RelayCommand]
    private void DismissLoginRequired() => LoginRequiredMessage = null;

    // â”€â”€ Steam location â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    [ObservableProperty] private string _steamPath = "";
    [ObservableProperty] private bool _isSteamOverridden;
    [ObservableProperty] private string _steamSource = "";
    [ObservableProperty] private string? _steamWarning;

    // â”€â”€ Install behavior â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    /// <summary>Auto Update Apps (Don't Lock Manifests). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _autoUpdateApps;

    partial void OnAutoUpdateAppsChanged(bool value) => _settings.AutoUpdateApps = value;

    /// <summary>FastFetch: auto-download from the first available source. Same persisted setting the Add
    /// screen's toggle drives (both read/write SettingsService.FastFetch).</summary>
    [ObservableProperty] private bool _fastFetch;

    partial void OnFastFetchChanged(bool value) => _settings.FastFetch = value;

    /// <summary>Donate spare Steam decryption keys to the community pool. Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _donateKeys;

    partial void OnDonateKeysChanged(bool value) => _settings.DonateKeys = value;

    // â”€â”€ DNS resolution â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public ObservableCollection<DnsModeOption> DnsModeOptions { get; } =
    [
        new DnsModeOption("Automatic (fallback on failure)", "Auto"),
        new DnsModeOption("Always use Cloudflare", "Always"),
        new DnsModeOption("Use system DNS only", "Never")
    ];

    [ObservableProperty] private DnsModeOption _selectedDnsMode;

    partial void OnSelectedDnsModeChanged(DnsModeOption value)
    {
        if (value != null)
            _settings.DnsMode = value.Tag;
    }

    // â”€â”€ Startup behavior â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "DataJackUI";

    /// <summary>Launch the app on Windows sign-in (writes HKCU â€¦\Run). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _startWithWindows;

    partial void OnStartWithWindowsChanged(bool value)
    {
        _settings.StartWithWindows = value;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (value)
                // --minimized: start silently in the tray on sign-in so it just serves the
                // local backend (127.0.0.1:6767) for the Steam plugin without stealing focus.
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --minimized");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { /* registry write blocked. Setting is still saved, just not applied this run */ }
    }

    /// <summary>Minimize to the system tray instead of the taskbar. Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _minimizeToTray;

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        if (!value) RequestShowWindow?.Invoke(); // turning it off restores the window if it's hidden in the tray
    }

    /// <summary>Set by App: restore the main window from the tray (used when Minimize-to-tray is turned off).</summary>
    public Action? RequestShowWindow { get; set; }

    // â”€â”€ Steam Web API Key & Helper Tools â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    [ObservableProperty] private string _steamWebApiKey = "";
    partial void OnSteamWebApiKeyChanged(string value) => _settings.SteamWebApiKey = value;

    [ObservableProperty] private string _toolsPath = "";
    partial void OnToolsPathChanged(string value) => _settings.ToolsPath = value;

    // â”€â”€ Cloud Saves backup folder â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    [ObservableProperty] private string _cloudSavesBackupPath = "";
    [ObservableProperty] private bool _hasCustomCloudSavesBackupPath;

    partial void OnCloudSavesBackupPathChanged(string value)
    {
        _settings.CloudSavesLocalPath = string.IsNullOrWhiteSpace(value) ? null : value;
        HasCustomCloudSavesBackupPath = !string.IsNullOrWhiteSpace(_settings.CloudSavesLocalPath);
    }

    [RelayCommand]
    private void BrowseCloudSavesBackupFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Destination Folder for Local Backups",
            InitialDirectory = Directory.Exists(EffectiveCloudSavesBackupPath) ? EffectiveCloudSavesBackupPath : ""
        };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            CloudSavesBackupPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void ResetCloudSavesBackupFolder()
    {
        CloudSavesBackupPath = "";
    }

    [RelayCommand]
    private void OpenCloudSavesBackupFolder()
    {
        string dir = EffectiveCloudSavesBackupPath;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { }
    }

    public string EffectiveCloudSavesBackupPath =>
        !string.IsNullOrWhiteSpace(_settings.CloudSavesLocalPath)
            ? _settings.CloudSavesLocalPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUI", "save_backups");

    // â”€â”€ Language â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    /// <summary>Available UI languages (native endonyms, matching Steam's list). "System default"
    /// (null tag) follows Windows. Languages whose .resx isn't present fall back to English.</summary>
    public ObservableCollection<LanguageOption> LanguageOptions { get; } =
    [
        new(Resources.Strings.Settings_Language_SystemDefault, null),
        new("English", "en"),
    ];

    [ObservableProperty] private LanguageOption _selectedLanguage = null!;

    private bool _suppressLanguagePrompt; // true during ctor init so we don't prompt on first bind

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value is null || _suppressLanguagePrompt) return;
        _settings.Language = value.Tag; // null = follow system
        // The whole UI is built with parse-time x:Static resources, so a relaunch is needed to re-read it.
        RequestRestartPrompt?.Invoke();
    }

    /// <summary>Set by App so the guest "Sign in" button can run the Discord flow.</summary>
    public Func<Task>? RequestSignIn { get; set; }

    /// <summary>Set by App: actually relaunch the app (used after a language change).</summary>
    public Action? RequestRestart { get; set; }

    /// <summary>Show the "language changed. Restart now?" toast. Wired here so the VM stays UI-agnostic;
    /// App provides the toast + restart action.</summary>
    public Action? RequestRestartPrompt { get; set; }

    public SettingsViewModel(SettingsService settings, AuthService auth, SteamService steam,
        UpdateService updateService, UnlockerService? unlocker = null)
    {
        _settings = settings;
        _auth = auth;
        _steam = steam;
        _updateService = updateService;
        _unlocker = unlocker;
        _auth.AuthStateChanged += RefreshAccount;
        RefreshAccount();
        RefreshSteam();
        if (_unlocker is not null)
        {
            _ = CheckIceBreakerUpdatesAsync();
        }
        _autoUpdateApps = settings.AutoUpdateApps; // init from saved value (default ON) without triggering Save
        _fastFetch = settings.FastFetch;
        _donateKeys = settings.DonateKeys;
        _startWithWindows = settings.StartWithWindows; // default OFF. Init without triggering the registry write
        _minimizeToTray = settings.MinimizeToTray;
        _steamWebApiKey = settings.SteamWebApiKey ?? "";
        _toolsPath = settings.ToolsPath ?? "";
        _cloudSavesBackupPath = settings.CloudSavesLocalPath ?? "";
        _hasCustomCloudSavesBackupPath = !string.IsNullOrWhiteSpace(settings.CloudSavesLocalPath);

        // Select the saved language (or "System default") without firing the restart prompt.
        _suppressLanguagePrompt = true;
        _selectedLanguage = LanguageOptions.FirstOrDefault(o => o.Tag == settings.Language) ?? LanguageOptions[0];
        _suppressLanguagePrompt = false;

        _selectedDnsMode = DnsModeOptions.FirstOrDefault(o => o.Tag == settings.DnsMode) ?? DnsModeOptions[0];
    }

    private void RefreshSteam()
    {
        string? path = _steam.EffectivePath;
        IsSteamOverridden = _steam.IsOverridden;
        SteamPath = path ?? Resources.Strings.Settings_SteamNotFound;
        SteamSource = IsSteamOverridden ? Resources.Strings.Settings_SteamSource_Custom
            : path is null ? Resources.Strings.Settings_SteamSource_NotFound
            : Resources.Strings.Settings_SteamSource_Auto;
        SteamWarning = path is not null && !_steam.IsValid ? Resources.Strings.Settings_SteamWarning_NoExe : null;
    }

    public void RefreshAccount()
    {
        void Update()
        {
            IsGuest = _auth.IsGuest;
            DisplayName = _auth.DisplayName;
            Username = _settings.CustomUsername ?? "anonymous";
            UserId = _auth.UserId ?? _auth.DiscordId;
            Email = _auth.Email;
            AvatarUrl = _auth.AvatarUrl;
            UpdateAvatarImage();
            OnPropertyChanged(nameof(HasCustomAvatar));
            if (!IsGuest) LoginRequiredMessage = null;
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Update);
        }
        else
        {
            Update();
        }
    }

    [RelayCommand]
    private void StartEditProfile() => IsEditingProfile = true;

    [RelayCommand]
    private void FinishEditProfile()
    {
        if (string.IsNullOrWhiteSpace(Username))
        {
            Username = "anonymous";
        }
        else
        {
            Username = Username.Trim();
        }
        IsEditingProfile = false;
    }

    [RelayCommand]
    private void ToggleEditProfile()
    {
        if (IsEditingProfile)
        {
            FinishEditProfile();
        }
        else
        {
            StartEditProfile();
        }
    }

    [RelayCommand]
    private void OpenAvatarDialog()
    {
        AvatarUrlInput = _settings.CustomAvatarPath?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true
            ? _settings.CustomAvatarPath
            : "";
        IsAvatarDialogOpen = true;
    }

    [RelayCommand]
    private void CloseAvatarDialog()
    {
        IsAvatarDialogOpen = false;
    }

    [RelayCommand]
    private void PickLocalAvatar()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose Profile Picture",
            Filter = "Image Files (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|All Files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                string source = dialog.FileName;
                string ext = Path.GetExtension(source);
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUIGui");
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, $"avatar{ext}");
                File.Copy(source, dest, overwrite: true);
                _settings.CustomAvatarPath = dest;
            }
            catch
            {
                _settings.CustomAvatarPath = dialog.FileName;
            }

            UpdateAvatarImage();
            OnPropertyChanged(nameof(HasCustomAvatar));
            IsAvatarDialogOpen = false;
        }
    }

    [RelayCommand]
    private void ApplyAvatarUrl()
    {
        if (!string.IsNullOrWhiteSpace(AvatarUrlInput))
        {
            string url = AvatarUrlInput.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }
            _settings.CustomAvatarPath = url;
            UpdateAvatarImage();
            OnPropertyChanged(nameof(HasCustomAvatar));
            IsAvatarDialogOpen = false;
        }
    }

    [RelayCommand]
    private void ResetAvatar()
    {
        _settings.CustomAvatarPath = null;
        UpdateAvatarImage();
        OnPropertyChanged(nameof(HasCustomAvatar));
        IsAvatarDialogOpen = false;
    }

    private void UpdateAvatarImage()
    {
        string? pathOrUrl = _settings.CustomAvatarPath ?? _auth.AvatarUrl;
        if (string.IsNullOrWhiteSpace(pathOrUrl))
        {
            AvatarImage = null;
            return;
        }

        try
        {
            if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(pathOrUrl, UriKind.Absolute);
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                AvatarImage = bi;
            }
            else if (File.Exists(pathOrUrl))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(pathOrUrl, UriKind.Absolute);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                AvatarImage = bi;
            }
            else
            {
                AvatarImage = null;
            }
        }
        catch
        {
            AvatarImage = null;
        }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (RequestSignIn is not null) await RequestSignIn();
    }

    [RelayCommand]
    private void OverrideSteamFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Resources.Strings.Settings_ChooseSteamFolder,
            InitialDirectory = _steam.EffectivePath ?? "",
        };
        if (dialog.ShowDialog() == true)
        {
            _settings.SteamPathOverride = dialog.FolderName;
            RefreshSteam();
            if (_unlocker is not null)
            {
                _ = CheckIceBreakerUpdatesAsync();
            }
        }
    }

    [RelayCommand]
    private void ClearSteamOverride()
    {
        _settings.SteamPathOverride = null;
        RefreshSteam();
        if (_unlocker is not null)
        {
            _ = CheckIceBreakerUpdatesAsync();
        }
    }

    [RelayCommand]
    private void OpenSteamFolder()
    {
        string? path = _steam.EffectivePath;
        if (path is not null && System.IO.Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenWebsite() =>
        Process.Start(new ProcessStartInfo(AppConfig.ApiBaseUrl) { UseShellExecute = true });

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        IsDownloadingUpdate = false;
        DownloadProgress = 0;
        UpdateStatusText = "Checking for DataJackUI updatesâ€¦";
        UpdateStatusColor = "#9ca3af";

        try
        {
            var progress = new Progress<int>(percent =>
            {
                IsDownloadingUpdate = true;
                DownloadProgress = percent;
                UpdateStatusText = $"Downloading updateâ€¦ {percent}%";
            });

            var info = await _updateService.CheckAndAutoUpdateAsync(progress);

            if (info != null)
            {
                string ver = info.TargetFullRelease.Version.ToString();
                UpdateStatusText = $"Update v{ver} downloaded! It will apply automatically when DataJackUI closes.";
                UpdateStatusColor = "#22c55e";
                OnPropertyChanged(nameof(HasStagedUpdate));
            }
            else if (_updateService.HasStagedUpdate)
            {
                string? ver = _updateService.StagedUpdate?.TargetFullRelease?.Version?.ToString();
                UpdateStatusText = $"Update {(ver != null ? $"v{ver} " : "")}is downloaded and ready to apply on exit.";
                UpdateStatusColor = "#22c55e";
                OnPropertyChanged(nameof(HasStagedUpdate));
            }
            else
            {
                UpdateStatusText = "DataJackUI is up to date.";
                UpdateStatusColor = "#38bdf8";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Update check failed: {ex.Message}";
            UpdateStatusColor = "#f87171";
        }
        finally
        {
            IsCheckingForUpdates = false;
            IsDownloadingUpdate = false;
        }
    }

    public bool HasStagedUpdate => _updateService.HasStagedUpdate;

    [RelayCommand]
    private void ApplyUpdate()
    {
        if (_updateService.HasStagedUpdate)
        {
            _updateService.ApplyAndRestart();
        }
    }

    [RelayCommand]
    private async Task CheckIceBreakerUpdatesAsync()
    {
        if (IsCheckingIceBreaker || IsUpdatingIceBreaker || _unlocker is null) return;

        IsCheckingIceBreaker = true;
        IceBreakerStatusText = "Checking for IceBreaker updatesâ€¦";
        IceBreakerStatusColor = "#9ca3af";

        try
        {
            var state = await _unlocker.GetStateAsync(UnlockerMode.IceBreaker, forceRefresh: true);

            if (!string.IsNullOrWhiteSpace(state.LatestVersion))
            {
                IceBreakerVersionBadge = state.LatestVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                    ? state.LatestVersion
                    : $"v{state.LatestVersion}";
            }

            switch (state.Status)
            {
                case ModeStatus.NotInstalled:
                    IceBreakerUpdateAvailable = true;
                    IceBreakerStatusText = $"IceBreaker {IceBreakerVersionBadge ?? "latest"} is available to install.";
                    IceBreakerStatusColor = "#22c55e";
                    break;
                case ModeStatus.UpdateAvailable:
                    IceBreakerUpdateAvailable = true;
                    IceBreakerStatusText = $"Update {IceBreakerVersionBadge} is available.";
                    IceBreakerStatusColor = "#22c55e";
                    break;
                case ModeStatus.UpToDate:
                    IceBreakerUpdateAvailable = false;
                    IceBreakerStatusText = "IceBreaker is up to date.";
                    IceBreakerStatusColor = "#38bdf8";
                    break;
                default:
                    IceBreakerUpdateAvailable = false;
                    IceBreakerStatusText = "Could not check IceBreaker status (verify Steam folder).";
                    IceBreakerStatusColor = "#f87171";
                    break;
            }
        }
        catch (Exception ex)
        {
            IceBreakerStatusText = $"Update check failed: {ex.Message}";
            IceBreakerStatusColor = "#f87171";
        }
        finally
        {
            IsCheckingIceBreaker = false;
        }
    }

    [RelayCommand]
    private async Task UpdateIceBreakerAsync()
    {
        if (IsUpdatingIceBreaker || _unlocker is null) return;

        if (SteamService.IsSteamRunning())
        {
            bool closeSteam = await UiMessageBox.ShowConfirmAsync(
                "Close Steam to Update IceBreaker",
                "Steam is currently running and has version.dll loaded.\n\nDataJackUI needs to close Steam to update IceBreaker, then you can reopen Steam afterward.\n\nClose Steam and update now?",
                "Close Steam & Update",
                "Cancel");

            if (!closeSteam) return;

            IceBreakerStatusText = "Closing Steam…";
            IceBreakerStatusColor = "#9ca3af";
            await Task.Run(_steam.StopSteam);
        }

        IsUpdatingIceBreaker = true;
        IceBreakerDownloadProgress = 0;
        IceBreakerStatusText = "Updating IceBreakerâ€¦";
        IceBreakerStatusColor = "#9ca3af";

        try
        {
            var progress = new Progress<double?>(p =>
            {
                if (p.HasValue)
                {
                    IceBreakerDownloadProgress = p.Value * 100.0;
                }
            });

            var result = await _unlocker.InstallAsync(UnlockerMode.IceBreaker, progress);
            if (result.Success)
            {
                IceBreakerDownloadProgress = 100;
                IceBreakerUpdateAvailable = false;
                var state = await _unlocker.GetStateAsync(UnlockerMode.IceBreaker);
                if (!string.IsNullOrWhiteSpace(state.LatestVersion))
                {
                    IceBreakerVersionBadge = state.LatestVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                        ? state.LatestVersion
                        : $"v{state.LatestVersion}";
                }
                IceBreakerStatusText = "IceBreaker updated! Restart Steam to load the new DLL.";
                IceBreakerStatusColor = "#22c55e";
            }
            else
            {
                IceBreakerStatusText = $"Update failed: {result.Error}";
                IceBreakerStatusColor = "#f87171";
            }
        }
        catch (Exception ex)
        {
            IceBreakerStatusText = $"Update failed: {ex.Message}";
            IceBreakerStatusColor = "#f87171";
        }
        finally
        {
            IsUpdatingIceBreaker = false;
        }
    }

    [RelayCommand]
    private void SignOut() => _auth.SignOut();

    /// <summary>Called by the View when loaded.</summary>
    public void OnViewLoaded()
    {
        RefreshAccount();
        // Re-sync FastFetch in case the Add screen's toggle changed it this session (both are singletons
        // that only read the setting at construction). No-op if unchanged; a real change writes back the
        // same value, so no feedback loop.
        FastFetch = _settings.FastFetch;
    }
}
