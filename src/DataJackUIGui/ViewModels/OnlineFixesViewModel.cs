using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataJackUIGui.Models;
using DataJackUIGui.Services;
using DataJackUIGui.Services.Downloads;

namespace DataJackUIGui.ViewModels;

public partial class OnlineFixGameCardVm : FixGameCardVm
{
    private readonly OnlineFixesViewModel _parent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFix), nameof(CanRevert))]
    private bool _isApplied;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFix), nameof(CanRevert))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _notes;

    [ObservableProperty]
    private bool _isLoadingNotes;

    [ObservableProperty]
    private DownloadItem? _downloadItem;

    public bool CanFix => !IsApplied && !IsBusy;
    public bool CanRevert => IsApplied && !IsBusy;

    public OnlineFixGameCardVm(DenuvoGameListing g, CoverCache? covers, OnlineFixesViewModel parent)
        : base(g, covers)
    {
        _parent = parent;
    }

    [RelayCommand]
    private async Task Fix()
    {
        await _parent.ApplyCardFixAsync(this);
    }

    [RelayCommand]
    private async Task Revert()
    {
        await _parent.RevertCardFixAsync(this);
    }

    [RelayCommand]
    private async Task ShowNotes()
    {
        await _parent.ShowCardNotesAsync(this);
    }
}

public partial class OnlineFixesViewModel : FixesViewModel
{
    private readonly DataJackUIApiClient _api;
    private readonly CoverCache _covers;
    private readonly SteamService _steam;
    private readonly SteamLibraryService _library;
    private readonly ManifestJobFactory _jobs;
    private readonly DownloadQueue _queue;
    private readonly ToastService _toast;
    private readonly AuthService _auth;
    private readonly SteamAppListCache _appList;

    public OnlineFixesViewModel(
        DataJackUIApiClient api,
        AuthService auth,
        CoverCache covers,
        SteamAppInfoCache appInfo,
        SteamAppListCache appList,
        ToastService toast,
        SettingsService settings,
        DownloadQueue queue,
        ManifestJobFactory jobs,
        SteamLibraryService library,
        SteamService steam)
        : base(api, auth, covers, appInfo, appList, toast, settings, queue, jobs, library, steam)
    {
        _api = api;
        _auth = auth;
        _covers = covers;
        _steam = steam;
        _library = library;
        _jobs = jobs;
        _queue = queue;
        _toast = toast;
        _appList = appList;
    }

    public virtual string HeaderTitle => Resources.Strings.Nav_OnlineFix;

    public override async Task LoadAsync(bool force = false)
    {
        if (!force && _allGames.Count > 0) return;
        IsLoading = true;
        try
        {
            var data = await _api.GetOnlineFixListingsAsync();
            if (data is null)
            {
                EmptyMessage = Resources.Strings.Fixes_Err_Load;
                return;
            }

            _allGames = data.Games.Select(g => (FixGameCardVm)new OnlineFixGameCardVm(g, _covers, this)).ToList();
            PopulateTags();

            _installedAppIds = _steam.IsValid
                ? await Task.Run(() => LuaInstaller.EnumerateInstalled(_steam).Select(i => i.AppId).ToHashSet())
                : [];
            OnPropertyChanged(nameof(MyGamesHint));

            ApplyFilter();
            if (_allGames.Count == 0) EmptyMessage = Resources.Strings.Fixes_Empty_None;
        }
        catch
        {
            EmptyMessage = Resources.Strings.Fixes_Err_Load;
        }
        finally
        {
            IsLoading = false;
            Loaded = true;
        }
    }

    protected override void OnPageSliced(IReadOnlyList<FixGameCardVm> slice)
    {
        base.OnPageSliced(slice);

        // Check applied fix status for currently visible slice
        Task.Run(() =>
        {
            foreach (var card in slice.OfType<OnlineFixGameCardVm>())
            {
                long appId = ResolveAppId(card);
                if (appId > 0)
                {
                    bool applied = _jobs.HasAppliedFix(appId, card.AppId);
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher is not null && !dispatcher.CheckAccess())
                    {
                        dispatcher.Invoke(() => card.IsApplied = applied);
                    }
                    else
                    {
                        card.IsApplied = applied;
                    }
                }
            }
        });
    }

    public long ResolveAppId(FixGameCardVm card)
    {
        if (long.TryParse(card.AppId, out long numericId))
            return numericId;
        if (long.TryParse(card.DisplayAppId, out long displayId))
            return displayId;

        if (_appList is not null && !string.IsNullOrWhiteSpace(card.Name))
        {
            var matches = _appList.Search(card.Name, 1);
            if (matches.Count > 0)
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is not null && !dispatcher.CheckAccess())
                    dispatcher.Invoke(() => card.DisplayAppId = matches[0].AppId.ToString());
                else
                    card.DisplayAppId = matches[0].AppId.ToString();
                return matches[0].AppId;
            }
        }

        return 0;
    }

    public async Task ShowCardNotesAsync(OnlineFixGameCardVm card)
    {
        if (!string.IsNullOrWhiteSpace(card.Notes))
        {
            await UiMessageBox.ShowConfirmAsync(
                $"{card.Name} - Setup Notes",
                card.Notes,
                "OK",
                "");
            return;
        }

        card.IsLoadingNotes = true;
        try
        {
            string? notes = null;
            if (!string.IsNullOrWhiteSpace(card.OnlineUrl))
            {
                notes = await _api.GetOnlineFixNotesAsync(card.OnlineUrl);
            }

            if (string.IsNullOrWhiteSpace(notes))
            {
                long appId = ResolveAppId(card);
                string searchKey = appId > 0 ? appId.ToString() : card.AppId;
                var scrape = await _api.ScrapeOnlineFixArticleUrlAsync(searchKey, card.OnlineUrl ?? "", card.Name);
                notes = scrape?.Notes;
            }

            if (!string.IsNullOrWhiteSpace(notes))
            {
                card.Notes = notes;
            }

            await UiMessageBox.ShowConfirmAsync(
                $"{card.Name} - Setup Notes",
                !string.IsNullOrWhiteSpace(notes) ? notes : "No special setup notes found for this game.",
                "OK",
                "");
        }
        catch (Exception ex)
        {
            await UiMessageBox.ShowConfirmAsync(
                $"{card.Name} - Setup Notes",
                $"Unable to load setup notes: {ex.Message}",
                "OK",
                "");
        }
        finally
        {
            card.IsLoadingNotes = false;
        }
    }

    public async Task ApplyCardFixAsync(OnlineFixGameCardVm card)
    {
        if (card.IsBusy) return;

        long appId = ResolveAppId(card);
        string? installDir = appId > 0 ? _library.GetInstallDir(appId) : null;
        if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
        {
            _toast.Show(
                Resources.Strings.Fixes_Toast_GameNotFound,
                string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, card.Name),
                error: true);
            return;
        }

        card.IsBusy = true;
        try
        {
            // Resolve online fix direct links
            string searchKey = appId > 0 ? appId.ToString() : card.AppId;
            var scrape = !string.IsNullOrWhiteSpace(card.OnlineUrl)
                ? await _api.ScrapeOnlineFixArticleUrlAsync(searchKey, card.OnlineUrl, card.Name)
                : await _api.ScrapeOnlineFixForGameAsync(searchKey, card.Name);

            string fixId = card.AppId;
            string fallbackName = scrape?.FixFileName ?? $"{card.AppId}_fix.zip";
            string fixTitle = scrape?.Title ?? $"{card.Name} Online Fix";

            var job = _jobs.CreateDenuvoJob(
                fixId,
                "fix",
                fallbackName,
                appId,
                card.Name,
                fixTitle,
                onFinished: (item, result) =>
                {
                    card.IsBusy = false;
                    if (result is null && item.Status == DownloadStatus.Failed)
                    {
                        _toast.Show(
                            Resources.Strings.Fixes_Toast_DownloadFailed,
                            item.Message ?? Resources.Strings.Fixes_Toast_DownloadFailed_Body,
                            error: true);
                        return;
                    }

                    if (result?.Ok == true)
                    {
                        card.IsApplied = true;
                    }
                });

            var queueItem = _queue.Enqueue(job);
            card.DownloadItem = queueItem;
        }
        catch (Exception ex)
        {
            card.IsBusy = false;
            _toast.Show(Resources.Strings.Fixes_Toast_DownloadFailed, ex.Message, error: true);
        }
    }

    public async Task RevertCardFixAsync(OnlineFixGameCardVm card)
    {
        if (card.IsBusy) return;

        long appId = ResolveAppId(card);
        if (appId == 0) return;

        bool confirm = await UiMessageBox.ShowConfirmAsync(
            string.Format(Resources.Strings.Fixes_Revert_Confirm_Title, card.Name),
            string.Format(Resources.Strings.Fixes_Revert_Confirm_Body, card.Name),
            Resources.Strings.Fixes_Revert,
            Resources.Strings.Fixes_Revert_Cancel);

        if (!confirm) return;

        card.IsBusy = true;
        try
        {
            var result = await Task.Run(() => _jobs.RevertGameFix(appId, card.AppId, card.Name));
            if (result.Ok)
            {
                card.IsApplied = false;
            }
        }
        finally
        {
            card.IsBusy = false;
        }
    }
}
