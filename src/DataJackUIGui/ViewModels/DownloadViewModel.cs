using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataJackUIGui.Models;
using DataJackUIGui.Services;
using DataJackUIGui.Services.Downloads;

namespace DataJackUIGui.ViewModels;

public partial class SourceRowViewModel : ObservableObject
{
    private readonly DownloadViewModel _parent;

    public string Name { get; }
    public string DisplayName { get; }
    public string Status { get; }
    public string? DiscordUrl { get; }
    public bool NeedsKey { get; }

    public bool IsAvailable => Status == "available";
    public string StatusLabel => Status.ToUpperInvariant();

    /// <summary>Hide the status badge when availability is unknown (e.g. a Hubcap row with no key set.
    /// We can't check, so we don't show a misleading badge; the "needs key" hint covers it instead).</summary>
    public bool ShowStatus => Status != "unknown";

    /// <summary>Hubcap key not configured. Show a hint instead of a download button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isLocked;

    [ObservableProperty] private string? _statsText;
    [ObservableProperty] private bool _isSupporter;

    /// <summary>The queue item for this row's in-flight download, if any. The row's progress bar binds
    /// straight through to it, so the queue stays the only owner of download state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private DownloadItem? _queueItem;

    // Guard against re-queuing from a double-click while the item is still live. The queue's DedupeKey
    // would collapse it anyway; this just keeps the button from looking clickable.
    public bool CanDownload => IsAvailable && !IsLocked && QueueItem?.IsActive != true;

    public SourceRowViewModel(DownloadViewModel parent, string name, string status)
    {
        _parent = parent;
        Name = name;
        Status = status;
        var meta = SourceMeta.Get(name);
        DisplayName = meta.DisplayName ?? name;
        DiscordUrl = meta.DiscordUrl;
        NeedsKey = meta.RequiresUserKey;
    }

    [RelayCommand]
    private async Task DownloadAsync() => await _parent.DownloadFromSourceAsync(this);

    [RelayCommand]
    private void OpenDiscord()
    {
        if (DiscordUrl is not null)
            Process.Start(new ProcessStartInfo(DiscordUrl) { UseShellExecute = true });
    }
}

/// <summary>One row in the overwrite-confirm diff (a depot/DLC the new lua adds or removes).</summary>
public record DiffRow(string Title, string Meta, bool IsDlc, bool IsShared, string SteamDbUrl);

/// <summary>A featured-game card on the Add page (Steam top-sellers / new-releases strips).</summary>
public record FeaturedItem(long AppId, string Name, string? Image);

/// <summary>A game card in the Add tab search results grid.</summary>
public partial class AddGameCardVm : ObservableObject
{
    public long AppId { get; }
    public string Name { get; }
    public string HeaderImage => $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{AppId}/header.jpg";

    [ObservableProperty] private string? _cover;
    private int _resolving;

    public AddGameCardVm(SteamSearchResult r)
    {
        AppId = r.AppId;
        Name = r.Name;
    }

    public async Task EnsureCoverAsync(CoverCache covers)
    {
        if (Cover is not null) return;
        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;
        try
        {
            string? local = covers.GetLocalPath(AppId) ?? await covers.EnsureAsync(AppId, HeaderImage);
            if (local is not null)
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is not null && !dispatcher.CheckAccess())
                    dispatcher.Invoke(() => Cover = local);
                else
                    Cover = local;
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _resolving, 0); }
    }
}

public partial class DownloadViewModel : PagedListViewModel<AddGameCardVm>
{
    private readonly DataJackUIApiClient _api;
    private readonly HubcapService _hubcap;
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly ToastService _toast;
    private readonly LuaInstaller _installer;
    private readonly SteamAppListCache _appList;
    private readonly SteamAppInfoCache _appInfo;
    private readonly SteamDepotInfo _depotInfo;
    private readonly HardwareAppIdService _hardware;
    private readonly DownloadQueue _queue;
    private readonly ManifestJobFactory _jobs;
    private readonly CoverCache _covers;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailsCts;

    // Per-confirm steamcmd lookup: depot/DLC id → its real depot info (name/size/os/lang).
    private IReadOnlyDictionary<long, ContentDepot> _depotsById = new Dictionary<long, ContentDepot>();

    /// <summary>Set by App so a guest hitting "Download" can be sent through the Discord sign-in flow.</summary>
    public Func<Task>? RequestSignIn { get; set; }

    /// <summary>Set by App: navigate to Manage and open this appid's detail (the install banner's "Reveal").</summary>
    public Action<long>? NavigateToGame { get; set; }

    public ObservableCollection<SourceRowViewModel> Sources { get; } = [];
    public ObservableCollection<DlcDepot> DlcDepots { get; } = [];

    // ── Featured strips (Steam top-sellers / new-releases), shown when the page is idle ──
    public ObservableCollection<FeaturedItem> TopSellers { get; } = [];
    public ObservableCollection<FeaturedItem> NewReleases { get; } = [];

    // Per-row visibility so an empty category collapses (raised in LoadFeaturedAsync).
    public bool HasTopSellers => TopSellers.Count > 0;
    public bool HasNewReleases => NewReleases.Count > 0;

    public bool ShowFeatured => false;

    [ObservableProperty]
    private string _searchText = "";
    [ObservableProperty] private bool _isSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    [NotifyPropertyChangedFor(nameof(ShowFeatured))]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    private GameDetails? _details;

    public bool HasDetails => Details is not null;
    public string GenresText => Details is null ? "" : string.Join(", ", Details.Genres);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    private bool _isChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSources))]
    [NotifyPropertyChangedFor(nameof(ShowFeatured))]
    private bool _sourcesLoaded;

    public bool HasSources => SourcesLoaded && Sources.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDlcInfo))]
    [NotifyPropertyChangedFor(nameof(DlcComplete))]
    [NotifyPropertyChangedFor(nameof(CanGenerateDlc))]
    private DlcInfo? _dlcInfo;

    public bool HasDlcInfo => DlcInfo is not null;
    public bool DlcComplete => DlcInfo?.MissingCount == 0;
    // haveCount > 0 → keys exist; missingCount == 0 → addappid alone suffices (mirrors the website rule)
    public bool CanGenerateDlc => DlcInfo is not null && (DlcInfo.HaveCount > 0 || DlcInfo.MissingCount == 0);

    /// <summary>True while the DLC job is queued or running (drives the button's spinner/disabled state).</summary>
    public bool IsGenerating => DlcQueueItem?.IsActive == true;
    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastDownload))]
    private DownloadedFile? _lastDownload;

    public bool HasLastDownload => LastDownload is not null;

    /// <summary>Drag-and-drop installer shown on the Add page.</summary>
    public DropInstallViewModel Drop { get; }

    // Set while a datajackui://install/silent/<id> install runs so the download path skips the interactive
    // overwrite-confirm overlay (there's no surfaced window for the user to confirm it on).
    private bool _silentInstall;

    /// <summary>
    /// Called by the protocol handler for datajackui://install/&lt;appid&gt;. Seeds the app, fetches details,
    /// enables FastFetch, and auto-fires the download. When <paramref name="onComplete"/> is supplied
    /// (the silent variant) the whole fetch→download→install chain runs headless and the final outcome
    /// is reported back via the callback (message, isError) so the caller can pop a tray notification.
    /// </summary>
    public async Task ProtocolInstall(long appId, Action<string, bool>? onComplete = null)
    {
        _silentInstall = onComplete is not null;
        // Cleared so the completion await below can't latch onto a PREVIOUS protocol install's item and
        // report its stale outcome when this one enqueues nothing (no sources, or the fetch failed).
        _lastEnqueued = null;
        FastFetch = true;
        _suppressSearch = true;
        SearchText = appId.ToString();
        _suppressSearch = false;
        ResetResults();

        try
        {
            Details = await _api.GetDetailsAsync(appId.ToString());
            OnPropertyChanged(nameof(GenresText));
        }
        catch
        {
            onComplete?.Invoke(Resources.Strings.Add_Err_Generic, true);
            _silentInstall = false;
            return;
        }

        if (HasDetails)
            await FetchCommand.ExecuteAsync(null);

        // The download is no longer inline: FetchAsync only ENQUEUES it. Wait for the queue item to
        // reach a terminal state before reporting, otherwise the caller's tray balloon fires early and
        // (on a cold silent launch) App's post-balloon shutdown timer can kill the app mid-download.
        if (onComplete is not null)
        {
            var item = _lastEnqueued;
            if (item is not null) await item.Completion;

            if (InstallStatus is not null)
                onComplete(InstallStatus, InstallFailed);
            else
                onComplete(Error ?? Resources.Strings.Add_Err_Generic, true);
            _silentInstall = false;
        }
    }

    // The most recent item this view model queued. ProtocolInstall awaits it so the silent-install
    // callback reflects the real outcome rather than "the download started".
    private DownloadItem? _lastEnqueued;

    // ── Steam-plugin headless add (reflected over HTTP; no window) ────
    /// <summary>Headless add driven by the Steam store plugin. Seeds the appid and runs the SAME
    /// FetchAsync pipeline the app UI uses (dynamic sources + Hubcap synth + key-gating + usage +
    /// FastFetch auto-download). The plugin polls state via the HTTP server and, when FastFetch is
    /// off, picks a source with <see cref="DownloadSourceByNameAsync"/>. Unlike ProtocolInstall this
    /// does NOT force FastFetch. It respects the user's setting so the plugin popup matches the app.</summary>
    public async Task StartPluginAddAsync(long appId)
    {
        _silentInstall = true; // headless: no surfaced window to confirm an overwrite on
        _suppressSearch = true;
        SearchText = appId.ToString();
        _suppressSearch = false;
        ResetResults();
        InstallStatus = null;
        Error = null;
        try
        {
            Details = await _api.GetDetailsAsync(appId.ToString());
            OnPropertyChanged(nameof(GenresText));
        }
        catch
        {
            Error = Resources.Strings.Add_Err_Generic;
            return;
        }
        if (HasDetails) await FetchCommand.ExecuteAsync(null);
    }

    /// <summary>Plugin picked a source by name (FastFetch-off path). Download+install it headlessly.</summary>
    public Task DownloadSourceByNameAsync(string name)
    {
        _silentInstall = true;
        var row = Sources.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        return row is null ? Task.CompletedTask : DownloadFromSourceAsync(row);
    }

    // ── Install result banner ───────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallResult))]
    [NotifyPropertyChangedFor(nameof(ShowFeatured))]
    private string? _installStatus;

    [ObservableProperty] private bool _installFailed;
    public bool HasInstallResult => InstallStatus is not null;

    // The appid the banner's "Reveal" opens. Captured at install time so it survives the user clearing
    // the search box (which nulls Details) while the banner is still showing.
    private long? _installedAppId;

    // ── Overwrite confirm overlay (base game whose lua already exists) ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffAdded))]
    private List<DiffRow> _diffAdded = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffRemoved))]
    private List<DiffRow> _diffRemoved = [];

    [ObservableProperty] private bool _isConfirmingOverwrite;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string? _confirmNoChanges;

    public bool HasDiffAdded => DiffAdded.Count > 0;
    public bool HasDiffRemoved => DiffRemoved.Count > 0;

    // Set while an overwrite overlay is open; the confirm/cancel commands complete it, which unblocks
    // the queue job waiting in ConfirmOverwriteAsync.
    private TaskCompletionSource<bool>? _pendingConfirm;

    // Only one overlay can be shown at a time (there is a single set of overlay properties), so
    // simultaneous confirmations serialize here rather than overwriting each other's diff.
    private readonly SemaphoreSlim _confirmGate = new(1, 1);

    /// <summary>The in-flight DLC generate, so the DLC button can disable itself while it runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGenerating))]
    private DownloadItem? _dlcQueueItem;

    private bool _suppressSearch;
    private string? _fastFetchSource;

    [ObservableProperty] private bool _fastFetch;
    partial void OnFastFetchChanged(bool value) => _settings.FastFetch = value;

    /// <summary>Re-sync the FastFetch toggle from the saved setting when the Add view appears. The Settings
    /// page exposes the same toggle, and both VMs are singletons that otherwise only read it at startup.</summary>
    public void SyncFastFetch() => FastFetch = _settings.FastFetch;

    public DownloadViewModel(DataJackUIApiClient api, HubcapService hubcap, SettingsService settings,
        AuthService auth, ToastService toast, LuaInstaller installer,
        SteamAppListCache appList, SteamAppInfoCache appInfo, SteamDepotInfo depotInfo,
        HardwareAppIdService hardware, DropInstallViewModel drop,
        DownloadQueue queue, ManifestJobFactory jobs, CoverCache covers)
    {
        _api = api;
        _hubcap = hubcap;
        _settings = settings;
        _auth = auth;
        _toast = toast;
        _installer = installer;
        _appList = appList;
        _appInfo = appInfo;
        _depotInfo = depotInfo;
        _hardware = hardware;
        _queue = queue;
        _jobs = jobs;
        _covers = covers;
        Drop = drop;
        _fastFetch = settings.FastFetch;
        InitPageSize(settings.AddPageSize);
    }

    protected override void SavePageSizeSetting(int size) => _settings.AddPageSize = size;

    protected override void OnPageSliced(IReadOnlyList<AddGameCardVm> slice)
    {
        foreach (var g in slice) _ = g.EnsureCoverAsync(_covers);
    }

    /// <summary>
    /// Pre-fill from the Manage page "Update" action and fetch the app's details directly.
    /// </summary>
    public void SeedSearch(long appId)
    {
        _suppressSearch = true;
        SearchText = appId.ToString();
        _suppressSearch = false;

        ResetResults();
        Details = null;
        _ = SearchDebouncedAsync(appId.ToString());
        _ = FetchDetailsDebouncedAsync(appId.ToString());
    }

    /// <summary>Guests can browse but must sign in for gated actions. Returns true if a sign-in was triggered.</summary>
    private async Task<bool> PromptSignInIfGuestAsync(string message)
    {
        if (!_auth.IsGuest) return false;
        Error = message;
        _toast.Show(Resources.Strings.Fixes_SignInRequired, message);
        if (RequestSignIn is not null) await RequestSignIn();
        return true;
    }

    // ── Search ──────────────────────────────────────────────────────

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressSearch) return;

        ResetResults();
        Details = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            _searchCts?.Cancel();
            SetFiltered([]);
            InstallStatus = null;
            Error = null;
            IsSearching = false;
            return;
        }

        string? appid = ExtractAppId(value);
        if (appid is not null)
        {
            _ = FetchDetailsDebouncedAsync(appid);
        }

        _ = SearchDebouncedAsync(value);
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.Invoke(action);
        else
            action();
    }

    private async Task SearchDebouncedAsync(string query)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(350, cts.Token);
            string q = query.Trim();
            if (string.IsNullOrWhiteSpace(q))
            {
                SetFiltered([]);
                RunOnUi(() => IsSearching = false);
                return;
            }

            RunOnUi(() => IsSearching = true);
            var results = await _api.SearchAsync(q, cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            var cards = results
                .Where(r => !_hardware.IsBlacklisted(r.AppId))
                .Select(r => new AddGameCardVm(r))
                .ToList();

            string? appid = ExtractAppId(q);
            if (appid is not null && long.TryParse(appid, out long parsedAppId))
            {
                if (!cards.Any(c => c.AppId == parsedAppId))
                {
                    try
                    {
                        var details = await _api.GetDetailsAsync(appid, cts.Token);
                        if (details is not null && !_hardware.IsBlacklisted(details.AppId))
                        {
                            cards.Insert(0, new AddGameCardVm(new SteamSearchResult
                            {
                                AppId = details.AppId,
                                Name = details.Name
                            }));
                        }
                    }
                    catch { }
                }
            }

            SetFiltered(cards);
            if (cards.Count == 0) RunOnUi(() => EmptyMessage = Resources.Strings.Fixes_Empty_None);
        }
        catch (OperationCanceledException) { }
        catch (ApiException ex) { RunOnUi(() => Error = ex.Message); SetFiltered([]); }
        catch { SetFiltered([]); }
        finally
        {
            if (_searchCts == cts) RunOnUi(() => IsSearching = false);
        }
    }

    private async Task FetchDetailsDebouncedAsync(string appid)
    {
        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(400, cts.Token);
            var details = await _api.GetDetailsAsync(appid, cts.Token);
            RunOnUi(() =>
            {
                Details = details;
                OnPropertyChanged(nameof(GenresText));
            });
        }
        catch (OperationCanceledException) { }
        catch { RunOnUi(() => Details = null); }
    }

    [RelayCommand]
    private async Task SelectResultAsync(AddGameCardVm card)
    {
        if (card is null) return;
        ResetResults();

        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        try
        {
            Details = await _api.GetDetailsAsync(card.AppId.ToString(), cts.Token);
            OnPropertyChanged(nameof(GenresText));
        }
        catch (OperationCanceledException) { }
        catch { Details = null; }
    }

    /// <summary>Click a featured-strip card → load that game's details (same path as a search result).</summary>
    [RelayCommand]
    private async Task SelectFeaturedAsync(FeaturedItem item)
    {
        if (item is null) return;
        ResetResults();

        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        try
        {
            Details = await _api.GetDetailsAsync(item.AppId.ToString(), cts.Token);
            OnPropertyChanged(nameof(GenresText));
        }
        catch (OperationCanceledException) { }
        catch { Details = null; }
    }

    /// <summary>Fetch the Steam featured strips once (top sellers + new releases). Best-effort: on failure
    /// the collections stay empty and the strips simply don't render. Steam hardware (Deck, Index, …) is
    /// filtered out via the hardware blacklist.</summary>
    public async Task LoadFeaturedAsync()
    {
        if (TopSellers.Count > 0 || NewReleases.Count > 0) return; // already loaded
        await _hardware.EnsureFreshAsync(); // make sure the blacklist is current before filtering
        var (top, fresh) = await _api.GetFeaturedAsync();
        foreach (var i in top)
            if (!_hardware.IsBlacklisted(i.Id)) TopSellers.Add(new FeaturedItem(i.Id, i.Name, i.LargeCapsuleImage));
        foreach (var i in fresh)
            if (!_hardware.IsBlacklisted(i.Id)) NewReleases.Add(new FeaturedItem(i.Id, i.Name, i.LargeCapsuleImage));
        OnPropertyChanged(nameof(HasTopSellers));
        OnPropertyChanged(nameof(HasNewReleases));
        OnPropertyChanged(nameof(ShowFeatured));
    }

    [RelayCommand]
    private void CloseResults() => SetFiltered([]);

    // ── Fetch (sources or DLC info, depending on app type) ─────────

    private bool CanFetch() => HasDetails && !IsChecking;

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private async Task FetchAsync()
    {
        if (Details is null) return;
        if (Details.IsDlc && await PromptSignInIfGuestAsync(Resources.Strings.Add_SignIn_Dlc)) return;
        ResetResults();
        IsChecking = true;
        try
        {
            if (Details.IsDlc) await FetchDlcAsync();
            else await FetchGameAsync();
        }
        catch (ApiException ex) { Error = ex.Message; }
        catch (Exception) { Error = Resources.Strings.Add_Err_Generic; }
        finally { IsChecking = false; }
    }

    private async Task FetchDlcAsync()
    {
        if (Details!.BaseAppId is null)
        {
            Error = Resources.Strings.Add_Err_BaseGame;
            return;
        }
        DlcInfo = await _api.GetDlcInfoAsync(Details.AppId.ToString(), Details.BaseAppId);
        DlcDepots.Clear();
        foreach (var d in DlcInfo?.Depots.OrderByDescending(d => d.Included) ?? Enumerable.Empty<DlcDepot>())
            DlcDepots.Add(d);
    }

    private async Task FetchGameAsync()
    {
        var statuses = await _api.CheckSourcesAsync(Details!.AppId.ToString());
        await AddHubcapSourceAsync(statuses, Details.AppId.ToString());

        foreach (var (name, status) in statuses.OrderByDescending(kv => SourceMeta.Get(kv.Key).RequiresUserKey ? 1 : 0))
            Sources.Add(new SourceRowViewModel(this, name, status));

        await ApplyHubcapStateAsync();

        if (FastFetch)
        {
            var best = Sources.FirstOrDefault(s => s.CanDownload);
            if (best is null)
            {
                Error = Resources.Strings.Add_FastFetch_NoSource;
                return;
            }
            _fastFetchSource = best.DisplayName;
            await DownloadFromSourceAsync(best);
        }
        else
        {
            SourcesLoaded = true;
            await RefreshStandardUsageAsync();
        }
    }

    /// <summary>The Hubcap source name as the website/source-meta keys it.</summary>
    private const string HubcapSourceName = "Sadie (Morrenus)";

    /// <summary>
    /// Inject the Hubcap row into the source-status map. The manifest backend no longer returns it, so
    /// availability comes from a direct Hubcap status check using the user's own key. With no key we still
    /// surface the row (as "available") so it shows up locked with the "needs a key" hint.
    /// </summary>
    private async Task AddHubcapSourceAsync(Dictionary<string, string> statuses, string appid)
    {
        string? key = _settings.HubcapApiKey;
        if (string.IsNullOrEmpty(key))
        {
            // No key → we genuinely can't check (the status endpoint needs the key). Mark it "unknown"
            // so the row shows up locked with the "needs a key" hint and NO misleading availability badge.
            statuses[HubcapSourceName] = "unknown";
            return;
        }

        var status = await _hubcap.CheckStatusAsync(key, appid);
        statuses[HubcapSourceName] = status?.ManifestFileExists == true ? "available" : "unavailable";
    }

    private async Task ApplyHubcapStateAsync()
    {
        var keyRows = Sources.Where(s => s.NeedsKey).ToList();
        if (keyRows.Count == 0) return;

        // No key configured → lock the premium rows (the row shows the "needs Hubcap key" hint).
        string? key = _settings.HubcapApiKey;
        if (string.IsNullOrEmpty(key))
        {
            foreach (var row in keyRows) row.IsLocked = true;
            return;
        }

        var stats = await _hubcap.GetStatsAsync(key);
        foreach (var row in keyRows)
        {
            row.IsLocked = stats?.CanMakeRequests != true;
            if (stats is not null)
                row.StatsText = $"{stats.DailyUsage}/{stats.DailyLimit}";
        }
    }

    /// <summary>
    /// Refresh the standard "X/25" daily usage badge on every non-Hubcap source row (those count
    /// toward the lua.tools daily limit; Hubcap sources show their own X/800 instead). Signed-in only.
    /// </summary>
    public async Task RefreshStandardUsageAsync()
    {
        var standardRows = Sources.Where(s => !s.NeedsKey).ToList();
        if (standardRows.Count == 0) return;
        if (_auth.IsGuest) return;

        var usageTask = _api.GetStandardUsageAsync();
        var supporterTask = _api.GetSupporterStatusAsync();
        await Task.WhenAll(usageTask, supporterTask);

        var usage = usageTask.Result;
        bool isSupporter = supporterTask.Result?.IsSupporter == true;

        foreach (var row in standardRows)
        {
            row.IsSupporter = isSupporter;
            if (usage is null) continue;
            row.StatsText = isSupporter
                ? $"{usage.Used} / {Resources.Strings.Add_Unlimited}"
                : $"{usage.Used}/{usage.Limit}";
        }
    }

    // ── Downloads ───────────────────────────────────────────────────

    /// <summary>
    /// Base-game manifest zip. Builds a job and hands it to the shared queue; the download, the
    /// overwrite confirmation and the install all happen there.
    /// </summary>
    /// <remarks>
    /// Returns as soon as the item is queued, so several games can be added back to back. The old
    /// "one at a time" behaviour came from a <c>Sources.Any(s =&gt; s.IsDownloading)</c> gate; duplicate
    /// suppression is now the queue's DedupeKey, and the queue's concurrency cap (default 1) decides
    /// how many actually run at once.
    /// </remarks>
    public async Task<DownloadItem?> DownloadFromSourceAsync(SourceRowViewModel source)
    {
        if (Details is null) return null;

        // Hubcap downloads use the user's OWN key and never touch lua.tools, so a guest with a key
        // configured can download without signing in. Every other source still needs a lua.tools account.
        bool hubcapWithKey = source.NeedsKey && !string.IsNullOrEmpty(_settings.HubcapApiKey);
        if (!hubcapWithKey && await PromptSignInIfGuestAsync(Resources.Strings.Add_SignIn_Download)) return null;

        Error = null;
        LastDownload = null;
        InstallStatus = null;

        // Captured now, not read from Details later: once the download is backgrounded the user can load
        // a different game before the confirmation appears, and the dialog must still name THIS one.
        long appId = Details.AppId;
        string gameName = Details.Name;
        bool needsKey = source.NeedsKey;

        var job = _jobs.CreateManifestJob(
            appId, gameName, source.Name, needsKey,
            // Silent/headless installs have no surfaced window to confirm on, so they skip the gate.
            confirm: _silentInstall ? null : (file, _, ct) => ConfirmOverwriteAsync(file, appId, gameName, ct),
            onFinished: (item, result) => OnManifestFinished(item, result, needsKey),
            onReveal: () => NavigateToGame?.Invoke(appId));

        var queued = _queue.Enqueue(job);
        source.QueueItem = queued;
        _lastEnqueued = queued;
        return queued;
    }

    /// <summary>DLC lua: download and install silently (it's just an unlock, no confirm).</summary>
    [RelayCommand]
    private async Task GenerateDlcAsync()
    {
        if (Details?.BaseAppId is null) return;
        if (await PromptSignInIfGuestAsync(Resources.Strings.Add_SignIn_Download)) return;

        Error = null;
        LastDownload = null;
        InstallStatus = null;

        long appId = Details.AppId;
        var job = _jobs.CreateDlcJob(
            appId, Details.BaseAppId, Details.Name,
            onFinished: (item, result) => OnManifestFinished(item, result, needsKey: false),
            onReveal: () => NavigateToGame?.Invoke(appId));

        DlcQueueItem = _lastEnqueued = _queue.Enqueue(job);
    }

    /// <summary>
    /// Terminal callback for a manifest/DLC job: drive the install banner and refresh the usage badge.
    /// Runs on the dispatcher.
    /// </summary>
    private void OnManifestFinished(DownloadItem item, JobResult? result, bool needsKey)
    {
        _installedAppId = item.AppId; // the banner's "Reveal" target, even after the search is cleared

        if (result is null)
        {
            // Cancelled, or failed before the install phase. item.Message already holds the reason.
            if (item.Status == DownloadStatus.Failed) Error = item.Message;
            else { InstallStatus = item.Message; InstallFailed = false; }
        }
        else
        {
            InstallFailed = !result.Ok;
            InstallStatus = result.Message;
            if (result.Ok && _fastFetchSource is not null)
            {
                InstallStatus += " " + string.Format(Resources.Strings.Add_FastFetch_Via, _fastFetchSource);
                _fastFetchSource = null;
            }
        }

        // Usage just changed: Hubcap against the key's own quota, everything else against the 25/day.
        _ = needsKey ? ApplyHubcapStateAsync() : RefreshStandardUsageAsync();
    }

    // ── Install + overwrite confirm ─────────────────────────────────

    /// <summary>
    /// The queue's confirmation gate for a manifest whose lua is already installed: show the
    /// before/after diff overlay and block until the user answers. True installs, false discards.
    /// </summary>
    /// <remarks>
    /// <para>Called by <see cref="DownloadQueue"/> from a background thread, so everything here marshals
    /// to the dispatcher. While this is awaited the item has already released its concurrency slot, so
    /// an overlay the user ignores delays only its own install.</para>
    ///
    /// <para><paramref name="appId"/> and <paramref name="gameName"/> are passed in rather than read
    /// from <c>Details</c>: the user can load a different game while this download is in flight, and the
    /// overlay must describe the game that was actually downloaded.</para>
    ///
    /// <para>Only one overlay can be open at a time (there is a single set of overlay properties), so
    /// concurrent confirmations queue behind <c>_confirmGate</c>.</para>
    /// </remarks>
    private async Task<bool> ConfirmOverwriteAsync(
        DownloadedFile file, long appId, string gameName, CancellationToken ct)
    {
        // No existing lua → nothing to confirm against, install straight away.
        string? existing = _installer.ReadInstalledLua(appId);
        if (existing is null) return true;

        await _confirmGate.WaitAsync(ct);
        try
        {
            var oldLua = LuaFileParser.Parse(existing, appId);
            var newLua = ExtractLuaFromZip(file.FilePath, appId);
            if (newLua is null) return true; // can't diff (bare lua / unreadable zip) → just install

            var diff = LuaFileParser.Diff(oldLua, newLua);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await App.Current.Dispatcher.InvokeAsync(async () =>
            {
                // Names from caches + one steamcmd call (cached per app) → real names, sizes, OS, language.
                await _appList.EnsureLoadedAsync();
                _depotsById = await BuildDepotLookupAsync(appId);
                DiffAdded = diff.Added.Select(ToDiffRow).ToList();
                DiffRemoved = diff.Removed.Select(ToDiffRow).ToList();
                ConfirmTitle = string.Format(Resources.Strings.Add_Confirm_Replace, gameName);
                ConfirmNoChanges = diff.HasChanges ? null : Resources.Strings.Add_Confirm_NoChanges;
                _pendingConfirm = tcs;
                IsConfirmingOverwrite = true;

                // Lazily resolve any DLC rows still showing a bare id via appdetails, then rebuild.
                var unnamed = diff.Added.Concat(diff.Removed)
                    .Where(e => DiffDisplayName(e) is null)
                    .Select(e => e.Id).Distinct().ToList();
                if (unnamed.Count > 0)
                {
                    await Parallel.ForEachAsync(unnamed, new ParallelOptions { MaxDegreeOfParallelism = 4 },
                        async (id, _) => await _appInfo.ResolveAsync(id));
                    if (IsConfirmingOverwrite && ReferenceEquals(_pendingConfirm, tcs))
                    {
                        DiffAdded = diff.Added.Select(ToDiffRow).ToList();
                        DiffRemoved = diff.Removed.Select(ToDiffRow).ToList();
                    }
                }
            });

            // Cancelling the download while the overlay is open closes it and declines.
            await using var reg = ct.Register(() => tcs.TrySetResult(false));
            bool answer = await tcs.Task;

            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_pendingConfirm, tcs))
                {
                    _pendingConfirm = null;
                    IsConfirmingOverwrite = false;
                }
            });
            return answer;
        }
        finally
        {
            _confirmGate.Release();
        }
    }

    [RelayCommand]
    private void ConfirmOverwrite()
    {
        IsConfirmingOverwrite = false;
        _pendingConfirm?.TrySetResult(true);
    }

    [RelayCommand]
    private void CancelOverwrite()
    {
        IsConfirmingOverwrite = false;
        _pendingConfirm?.TrySetResult(false);
    }

    /// <summary>Fetch the app's depots from steamcmd (cached) and index by depot id + dlcappid.</summary>
    private async Task<IReadOnlyDictionary<long, ContentDepot>> BuildDepotLookupAsync(long appId)
    {
        var map = new Dictionary<long, ContentDepot>();
        var info = await _depotInfo.GetAsync(appId);
        if (info is null) return map;
        foreach (var d in info.Depots)
        {
            map[d.Id] = d;
            if (d.DlcAppId is { } dlc) map[dlc] = d;
        }
        return map;
    }

    /// <summary>Real Steam name for a lua entry (DLC name from caches, or lua comment); null if unknown.</summary>
    private string? DiffDisplayName(LuaEntry e)
    {
        var d = _depotsById.GetValueOrDefault(e.Id);
        if (d?.DlcAppId is { } dlc)
        {
            string? steamName = _appList.GetName(dlc) ?? _appInfo.GetCached(dlc)?.Name;
            if (steamName is not null) return steamName;
        }
        return _appList.GetName(e.Id) ?? _appInfo.GetCached(e.Id)?.Name ?? e.Comment;
    }

    /// <summary>Enriched diff row: real name + "id · size · OS · lang" + DLC/SHARED chip + SteamDB link.</summary>
    private DiffRow ToDiffRow(LuaEntry e)
    {
        var d = _depotsById.GetValueOrDefault(e.Id);
        bool isDlc = d?.IsDlc == true;
        bool isShared = d?.IsShared == true;

        string title = DiffDisplayName(e) ?? GetFallbackTitle(e, d, isDlc, isShared);

        var meta = new List<string> { e.Id.ToString() };
        if (d is { Size: > 0 }) meta.Add(FormatSize(d.Size));
        if (!string.IsNullOrWhiteSpace(d?.Os)) meta.Add(PrettyOs(d!.Os!));
        if (!string.IsNullOrWhiteSpace(d?.Language)) meta.Add(d!.Language!);

        string url = d?.DlcAppId is { } dlcId
            ? $"https://steamdb.info/app/{dlcId}/"
            : $"https://steamdb.info/depot/{e.Id}/";

        return new DiffRow(title, string.Join("  ·  ", meta), isDlc, isShared, url);
    }

    private static string GetFallbackTitle(LuaEntry e, ContentDepot? d, bool isDlc, bool isShared)
    {
        if (isDlc) return string.Format(Resources.Strings.Manage_DlcName, d!.DlcAppId);
        if (isShared) return Resources.Strings.Manage_SharedDepot;
        if (e.HasKey) return Resources.Strings.Manage_Depot;
        return string.Format(Resources.Strings.Manage_DlcName, e.Id);
    }

    [RelayCommand]
    private static void OpenSteamDb(DiffRow row) => SteamService.OpenUrl(row.SteamDbUrl);

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return $"{gb:0.##} GB";
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private static string PrettyOs(string os) => os switch
    {
        "windows" => "Windows",
        "macos" or "macosx" => "macOS",
        "linux" => "Linux",
        _ => os
    };

    /// <summary>Extract the .lua from a manifest zip and parse it (without installing). A bare-lua
    /// download (no zip wrapper) is parsed directly so the overwrite-diff still works for those sources.</summary>
    private static LuaContents? ExtractLuaFromZip(string zipPath, long appId)
    {
        try
        {
            // bare .lua → parse as-is. Same byte sniff the install path uses.
            if (!ManifestJobFactory.IsZip(zipPath)) return LuaFileParser.Parse(zipPath, appId);

            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var luaEntry = archive.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
            if (luaEntry is null) return null;

            string tmp = Path.Combine(Path.GetTempPath(), $"datajackui_diff_{appId}_{Guid.NewGuid():N}.lua");
            luaEntry.ExtractToFile(tmp, overwrite: true);
            try { return LuaFileParser.Parse(tmp, appId); }
            finally { try { File.Delete(tmp); } catch { /* temp cleanup best-effort */ } }
        }
        catch { return null; }
    }

    /// <summary>The install banner's "Reveal" → open this game in the Manage detail view.</summary>
    [RelayCommand]
    private void RevealInstalled()
    {
        if ((_installedAppId ?? Details?.AppId) is { } appId) NavigateToGame?.Invoke(appId);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void ResetResults()
    {
        Sources.Clear();
        SourcesLoaded = false;
        DlcInfo = null;
        DlcDepots.Clear();
        Error = null;
        LastDownload = null;
        _fastFetchSource = null;
    }

    /// <summary>
    /// Extract an appid from a Steam/SteamDB store URL (…/app/&lt;id&gt;), or from a bare number of 5+
    /// digits. Short numbers go through normal title search instead. Game titles like "007", "500"
    /// or "1942" are numbers too. Tradeoff: very old games with short appids (e.g. 500 = Left 4 Dead)
    /// won't auto-resolve from a bare number and must be searched or pasted as a URL.
    /// </summary>
    private static string? ExtractAppId(string input)
    {
        string trimmed = input.Trim();
        if (Regex.IsMatch(trimmed, @"^\d{5,}$")) return trimmed;
        var m = Regex.Match(trimmed, @"/app/(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
