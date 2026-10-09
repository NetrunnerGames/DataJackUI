using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataJackUIGui.Models;
using DataJackUIGui.Services;
using DataJackUIGui.Services.Downloads;

namespace DataJackUIGui.ViewModels;

/// <summary>A game card in the Fixes grid.</summary>
public partial class FixGameCardVm : ObservableObject
{
    public string AppId { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _displayAppId;
    public string? HeaderImage { get; }
    public int FixCount { get; }
    public IReadOnlyList<string> TagIds { get; }
    public string FixCountLabel => FixTypeLabel;

    public bool HasPlaceholderName =>
        string.IsNullOrWhiteSpace(Name) ||
        Name.StartsWith("App ", StringComparison.OrdinalIgnoreCase) ||
        long.TryParse(Name.Trim(), out _);

    /// <summary>Displays what kind of fix is available (e.g. "Bypass", "Online Fix", "Hypervisor").</summary>
    public string FixTypeLabel
    {
        get
        {
            var kinds = new List<string>();
            foreach (var t in g.Tags)
            {
                string id = (t.Id ?? t.Slug ?? "").ToLowerInvariant();
                if (id.Contains("online")) kinds.Add("Online Fix");
                else if (id.Contains("bypass")) kinds.Add("Bypass");
                else if (!string.IsNullOrWhiteSpace(t.Name) && !id.Contains("hypervisor")) kinds.Add(t.Name);
            }
            if (kinds.Count == 0) kinds.Add("Bypass");
            return string.Join(" • ", kinds.Distinct(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>Local cached cover path (set after CoverCache resolves it); bound via ImagePathToSource.</summary>
    [ObservableProperty] private string? _cover;
    private readonly DenuvoGameListing g;
    private int _resolving;

    public FixGameCardVm(DenuvoGameListing g, CoverCache? covers = null)
    {
        this.g = g;
        AppId = g.AppId;
        _displayAppId = g.AppId;
        _name = g.Name;
        HeaderImage = g.HeaderImage;
        FixCount = g.FixCount;
        TagIds = g.Tags.Select(t => t.Id).ToList();
        if (covers is not null && long.TryParse(AppId, out long appid))
        {
            _cover = covers.GetLocalPath(appid);
        }
    }

    public bool Matches(string q) =>
        Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        AppId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        DisplayAppId.Contains(q, StringComparison.OrdinalIgnoreCase);

    public bool MatchesTag(string tagIdOrSlug)
    {
        if (TagIds.Any(t => string.Equals(t, tagIdOrSlug, StringComparison.OrdinalIgnoreCase))) return true;

        string tag = tagIdOrSlug.ToLowerInvariant();
        if (tag == "bypass")
        {
            return g.Tags.Any(t => t.Id.Contains("bypass", StringComparison.OrdinalIgnoreCase)
                                || t.Name.Contains("bypass", StringComparison.OrdinalIgnoreCase)
                                || t.Slug.Contains("bypass", StringComparison.OrdinalIgnoreCase)
                                || t.Id.Contains("voices38", StringComparison.OrdinalIgnoreCase)
                                || t.Id.Contains("rockstar", StringComparison.OrdinalIgnoreCase)
                                || t.Id.Contains("ubisoft", StringComparison.OrdinalIgnoreCase)
                                || t.Id.Contains("generic", StringComparison.OrdinalIgnoreCase));
        }

        if (tag == "online")
        {
            return g.Tags.Any(t => t.Id.Contains("online", StringComparison.OrdinalIgnoreCase)
                                || t.Name.Contains("online", StringComparison.OrdinalIgnoreCase)
                                || t.Slug.Contains("online", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    /// <summary>Cache the header image to disk once (CoverCache, keyed by appid), and resolve title/appid if missing.</summary>
    public async Task EnsureCoverAsync(CoverCache covers, SteamAppInfoCache appInfo, SteamAppListCache? appList = null)
    {
        long appid = 0;
        if (!long.TryParse(AppId, out appid))
        {
            // For online fix or non-numeric appids (e.g. of_18246), resolve the actual Steam App ID via appList search
            if (appList is not null && !string.IsNullOrWhiteSpace(Name))
            {
                var matches = appList.Search(Name, 1);
                if (matches.Count > 0)
                {
                    appid = matches[0].AppId;
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher is not null && !dispatcher.CheckAccess())
                        dispatcher.Invoke(() => DisplayAppId = appid.ToString());
                    else
                        DisplayAppId = appid.ToString();
                }
            }
        }
        else
        {
            DisplayAppId = appid.ToString();
        }

        // If Name is a placeholder ("App 1963680"), immediately try cached names or fetch in background
        if (appid > 0 && HasPlaceholderName)
        {
            string? cachedName = appList?.GetName(appid) ?? appInfo.GetCached(appid)?.Name;
            if (!string.IsNullOrWhiteSpace(cachedName) && !cachedName.StartsWith("App ", StringComparison.OrdinalIgnoreCase))
            {
                UpdateName(cachedName);
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var info = await appInfo.ResolveAsync(appid);
                        if (!string.IsNullOrWhiteSpace(info?.Name))
                        {
                            UpdateName(info.Name);
                            appList?.AddOrUpdate(appid, info.Name);
                        }
                    }
                    catch { }
                });
            }
        }

        if (Cover is not null) return;
        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;
        try
        {
            string? local = appid > 0 ? covers.GetLocalPath(appid) : null;
            if (local is null)
            {
                string rawUrl = !string.IsNullOrWhiteSpace(HeaderImage)
                    ? HeaderImage!
                    : (appid > 0 ? SteamAppInfoCache.GuessHeaderImageUrl(appid) : "");

                if (!string.IsNullOrWhiteSpace(rawUrl))
                {
                    string sanitized = SteamCdnUrl.Sanitize(rawUrl) ?? rawUrl;
                    long coverKey = appid > 0 ? appid : (long)(AppId.GetHashCode() & 0x7FFFFFFF);
                    local = await covers.EnsureAsync(coverKey, sanitized);
                }

                if (local is null && appid > 0)
                {
                    var info = await appInfo.ResolveAsync(appid);
                    if (!string.IsNullOrWhiteSpace(info?.HeaderImage))
                    {
                        string fallbackSanitized = SteamCdnUrl.Sanitize(info.HeaderImage) ?? info.HeaderImage;
                        local = await covers.EnsureAsync(appid, fallbackSanitized);
                    }
                    if (!string.IsNullOrWhiteSpace(info?.Name) && HasPlaceholderName)
                    {
                        UpdateName(info.Name);
                        appList?.AddOrUpdate(appid, info.Name);
                    }
                }
            }

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

    private void UpdateName(string newName)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.Invoke(() => Name = newName);
        else
            Name = newName;
    }
}

/// <summary>A tag filter pill; IsSelected drives its active highlight.</summary>
public partial class TagPillVm(DenuvoTag t) : ObservableObject
{
    public string Id { get; } = t.Id;
    public string Name { get; } = t.Name;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>One fix (release) in the per-game flyout.</summary>
public partial class FixItemVm(DenuvoFix f) : ObservableObject
{
    public string Id { get; } = f.Id;
    public string Title { get; } = f.Title;
    public string? Description { get; } = f.Description;
    public IReadOnlyList<DenuvoTag> Tags { get; } = f.Tags;
    public bool HasManifest { get; } = f.HasManifest;
    public bool HasFix { get; } = f.HasFix || !f.HasManifest;
    public string? ManifestFilename { get; } = f.ManifestFilename;
    public string? FixFilename { get; } = f.FixFilename;
    public string DateLabel { get; } = FormatDate(f.CreatedAt);
    public string? SizeLabel { get; } = FormatSize(f.FileSize, f.SizeStr);
    public bool HasSize => !string.IsNullOrEmpty(SizeLabel);

    /// <summary>In-flight queue items for this fix's two slots. The buttons and their progress bars bind
    /// straight through, so the shared queue stays the only owner of download state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadManifest))]
    private DownloadItem? _manifestItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadFix))]
    private DownloadItem? _fixItem;

    /// <summary>
    /// Whether the game is installed on disk. Only the FIX slot cares: it extracts a zip into the game
    /// folder, so with no folder there is nothing to apply. The MANIFEST slot installs a lua and works
    /// whether or not the game is installed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadFix), nameof(FixHint))]

    private bool _gameInstalled;

    /// <summary>True when the fix has been applied (its revert record exists on disk). Drives the Revert button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApplied), nameof(CanDownloadFix), nameof(FixHint))]
    private bool _isApplied;

    public bool HasApplied => IsApplied;

    public bool CanDownloadManifest => HasManifest && ManifestItem?.IsActive != true;
    public bool CanDownloadFix => HasFix && !IsApplied && FixItem?.IsActive != true;

    /// <summary>Why the Fix button is greyed out, or null when it isn't.</summary>
    public string? FixHint =>
        IsApplied ? Resources.Strings.Fixes_Applied_Hint : null;

    private static string FormatDate(string? iso) =>
        DateTimeOffset.TryParse(iso, out var d) ? d.UtcDateTime.ToString("d MMM yyyy") : "";

    private static string? FormatSize(long? bytes, string? explicitSize)
    {
        if (!string.IsNullOrWhiteSpace(explicitSize) && !string.Equals(explicitSize, "Fix archive", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(explicitSize, out long parsedBytes))
                bytes = parsedBytes;
            else
                return explicitSize;
        }

        if (bytes is null or <= 0) return null;
        double b = bytes.Value;
        if (b >= 1024 * 1024 * 1024) return $"{b / (1024 * 1024 * 1024):0.##} GB";
        if (b >= 1024 * 1024) return $"{b / (1024 * 1024):0.#} MB";
        if (b >= 1024) return $"{b / 1024:0.#} KB";
        return $"{b} B";
    }
}

/// <summary>
/// "Fixes" page: browse games with Denuvo fixes (grid + search + tag filter), open a game to see its
/// fixes, and download a fix's manifest (force-locked lua install) or fix zip (extract into the game
/// folder if installed). Downloads are auth-gated and count toward the 25/day limit (server-side).
/// </summary>
public partial class FixesViewModel : PagedListViewModel<FixGameCardVm>
{
    private readonly DataJackUIApiClient api;
    private readonly AuthService auth;
    private readonly CoverCache covers;
    private readonly SteamAppInfoCache appInfo;
    private readonly SteamAppListCache appList;
    private readonly ToastService toast;
    private readonly SettingsService settings;
    private readonly DownloadQueue queue;
    private readonly ManifestJobFactory jobs;
    private readonly SteamLibraryService library;
    private readonly SteamService steam;

    public FixesViewModel(
        DataJackUIApiClient api, AuthService auth, CoverCache covers, SteamAppInfoCache appInfo,
        SteamAppListCache appList, ToastService toast,
        SettingsService settings, DownloadQueue queue, ManifestJobFactory jobs,
        SteamLibraryService library, SteamService steam)
    {
        this.api = api;
        this.auth = auth;
        this.covers = covers;
        this.appInfo = appInfo;
        this.appList = appList;
        this.toast = toast;
        this.settings = settings;
        this.queue = queue;
        this.jobs = jobs;
        this.library = library;
        this.steam = steam;
        InitPageSize(settings.FixesPageSize);
    }

    /// <summary>Set by App so a guest hitting a download is sent through the Discord sign-in flow.</summary>
    public Func<Task>? RequestSignIn { get; set; }

    // The master list; the displayed page slice lives in the base's Items collection.
    protected List<FixGameCardVm> _allGames = [];

    public ObservableCollection<TagPillVm> Tags { get; } = [];

    // IsLoading, EmptyMessage and the IsEmpty gating are inherited from PagedListViewModel<FixGameCardVm>.
    // Page size persists via SavePageSizeSetting below.
    protected override void SavePageSizeSetting(int size) => settings.FixesPageSize = size;

    /// <summary>Warm the cover images and resolve title for just the freshly-shown page (idempotent, off-UI).</summary>
    protected override void OnPageSliced(IReadOnlyList<FixGameCardVm> slice)
    {
        foreach (var g in slice) _ = g.EnsureCoverAsync(covers, appInfo, appList);
    }

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [ObservableProperty] private string? _selectedTagId; // null = "All"
    public ObservableCollection<string> SortOptions { get; } = ["A to Z", "Z to A"];
    [ObservableProperty] private string _selectedSort = "A to Z";
    partial void OnSelectedSortChanged(string value) => ApplyFilter();

    // Appids with a lua in Steam's config/stplug-in ("my games"), so the page can filter the fix
    // listing down to games the user actually owns. Empty when Steam isn't set up / no luas installed.
    protected HashSet<long> _installedAppIds = [];

    /// <summary>True once the listing has been fetched (set at the end of LoadAsync).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFilter))]
    private bool _loaded;

    /// <summary>Gates the filter pills ("my games" + tags) behind a finished, non-empty listing.</summary>
    public bool CanFilter => Loaded && _allGames.Count > 0;

    /// <summary>Only show fix games the user has added (a lua in stplug-in). Mirrors Manage's "my games".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MyGamesHint))]
    private bool _myGamesOnly;

    /// <summary>
    /// How many of the user's added games actually appear in the fix listing.
    /// </summary>
    /// <remarks>
    /// The count must be the INTERSECTION, not <c>_installedAppIds.Count</c>. The string reads "{0} of
    /// your games have fixes", but the raw count is every game with a lua added — so a library with 243
    /// added games advertised 243 fixes while the filtered grid showed a dozen. This mirrors exactly what
    /// <c>ApplyFilter</c> puts on screen when My games is on.
    /// </remarks>
    public string MyGamesHint
    {
        get
        {
            if (_installedAppIds.Count == 0) return Resources.Strings.Fixes_MyGames_NotInstalled;

            int withFixes = _allGames.Count(g =>
                long.TryParse(g.AppId, out long id) && _installedAppIds.Contains(id));
            return string.Format(Resources.Strings.Fixes_MyGames_Count, withFixes);
        }
    }

    partial void OnMyGamesOnlyChanged(bool value)
    {
        if (value)
        {
            SelectedTagId = null; // one filter at a time — turning "my games" on drops any tag
            foreach (var pill in Tags) pill.IsSelected = false;
        }
        ApplyFilter();
    }

    // ── Detail flyout ───────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    private FixGameCardVm? _selectedGame;

    public bool IsDetailOpen => SelectedGame is not null;
    public ObservableCollection<FixItemVm> Fixes { get; } = [];
    [ObservableProperty] private bool _isLoadingFixes;

    // Per-game tag filter (only meaningful when this game's fixes span multiple tags).
    private List<FixItemVm> _allFixes = [];
    public ObservableCollection<TagPillVm> FixTags { get; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixTags))]
    private string? _selectedFixTagId;
    public bool HasFixTags => FixTags.Count > 0;

    // Downloads are owned by the shared DownloadQueue; per-fix progress lives on FixItemVm. The page no
    // longer has an IsBusy gate, so several fixes can be queued without waiting for each other.

    // ── Load ─────────────────────────────────────────────────────────

    protected virtual List<FixGameCardVm> FilterLoadedGames(List<FixGameCardVm> all) => all;

    protected virtual void PopulateTags()
    {
        Tags.Clear();
        Tags.Add(new TagPillVm(new DenuvoTag { Id = "bypass", Name = "Bypass", Slug = "bypass" }));
        Tags.Add(new TagPillVm(new DenuvoTag { Id = "online", Name = "Online Fix", Slug = "online" }));
    }

    /// <param name="force">True to re-fetch even if already loaded (the Refresh button); otherwise the
    /// listing loads once per session.</param>
    public async Task LoadAsync(bool force = false)
    {
        if (!force && _allGames.Count > 0) return; // load once per session
        IsLoading = true;
        try
        {
            var data = await api.GetDenuvoListingsAsync();
            if (data is null)
            {
                EmptyMessage = Resources.Strings.Fixes_Err_Load;
                return;
            }

            var all = data.Games.Select(g => new FixGameCardVm(g, covers)).ToList();
            _allGames = FilterLoadedGames(all);

            PopulateTags();

            // "My games" filter source: the same stplug-in scan the Manage page uses, so the toggle
            // shows only games the user actually added. Scanned once per listing load.
            _installedAppIds = steam.IsValid
                ? await Task.Run(() => LuaInstaller.EnumerateInstalled(steam).Select(i => i.AppId).ToHashSet())
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
            Loaded = true; // gates the filter pills: they appear only once the listing settled
        }
    }

    [RelayCommand]
    private Task Refresh() => RefreshWithCooldownAsync(async () =>
    {
        if (SearchText.Length > 0) SearchText = ""; // reset filter → full list visible
        if (SelectedTagId is not null) SelectTag(SelectedTagId); // clear active tag (toggles off)
        if (MyGamesOnly) MyGamesOnly = false; // ditto for the "my games" filter
        await LoadAsync(force: true);
        toast.Show(Resources.Strings.Fixes_Toast_Refreshed_Title,
            string.Format(Resources.Strings.Fixes_Toast_Refreshed_Body, _allGames.Count));
    });

    [RelayCommand]
    private void SelectTag(string? tagId)
    {
        SelectedTagId = SelectedTagId == tagId ? null : tagId; // toggle off when re-clicked
        if (MyGamesOnly) MyGamesOnly = false; // one filter at a time — picking a tag drops "my games"
        foreach (var pill in Tags) pill.IsSelected = pill.Id == SelectedTagId;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string q = SearchText.Trim();
        IEnumerable<FixGameCardVm> shown = _allGames;
        if (SelectedTagId is { } tag) shown = shown.Where(g => g.MatchesTag(tag));
        if (MyGamesOnly) shown = shown.Where(g => long.TryParse(g.AppId, out long id) && _installedAppIds.Contains(id));
        if (q.Length > 0) shown = shown.Where(g => g.Matches(q));

        if (string.Equals(SelectedSort, "Z to A", StringComparison.OrdinalIgnoreCase))
            shown = shown.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase);
        else
            shown = shown.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase);

        // Hand the filtered list to the base: it slices the visible page and (via OnPageSliced) warms
        // that page's covers.
        SetFiltered(shown);
    }

    // ── Detail flyout ───────────────────────────────────────────────

    /// <summary>
    /// Open the detail flyout for a specific game by its Steam AppId. Loads the listing if needed,
    /// finds the game card, and opens its fix detail (the flyout makes its own per-appid API call).
    /// Used by the datajackui://fix/ protocol handler.
    /// </summary>
    public async Task OpenForAppIdAsync(long appId)
    {
        if (_allGames.Count == 0)
            await LoadAsync();

        var game = _allGames.FirstOrDefault(g => g.AppId == appId.ToString());
        if (game is null) return;

        SearchText = "";
        SelectedTagId = null;
        ApplyFilter();

        await OpenGame(game);
    }

    [RelayCommand]
    private void CopyAppId(FixGameCardVm game)
    {
        if (!SteamService.CopyToClipboard(game.AppId))
            toast.Show(Resources.Strings.Common_CopyAppId, Resources.Strings.Err_ClipboardBusy, error: true);
    }

    /// <summary>
    /// Open the game's Steam install folder — where <c>ApplyDenuvoFix</c> extracts a fix to.
    /// </summary>
    /// <remarks>
    /// Resolved on click, not bound to a property: <c>GetInstallDir</c> walks libraryfolders.vdf and the
    /// appmanifest files, which is far too much work to repeat for every card in a grid on every render.
    /// The cost of that is the action being offered for games that aren't installed, so it reports the
    /// same "game not found" toast the fix flow already uses rather than failing silently.
    /// </remarks>
    [RelayCommand]
    private void ShowInFolder(FixGameCardVm game)
    {
        string? dir = long.TryParse(game.AppId, out long appId) ? library.GetInstallDir(appId) : null;
        if (SteamService.ShowInExplorer(dir)) return;

        toast.Show(Resources.Strings.Fixes_Toast_GameNotFound,
            string.Format(Resources.Strings.Fixes_Toast_GameNotFound_Body, game.Name), error: true);
    }

    private static void ClearCursor()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.Invoke(() =>
        {
            System.Windows.Input.Mouse.Capture(null);
            System.Windows.Input.Mouse.OverrideCursor = null;
        });
    }

    [RelayCommand]
    private async Task OpenGame(FixGameCardVm game)
    {
        ClearCursor();
        SelectedGame = game;
        _ = game.EnsureCoverAsync(covers, appInfo, appList);
        if (game.HasPlaceholderName && long.TryParse(game.AppId, out long appid))
        {
            try
            {
                var info = await appInfo.ResolveAsync(appid);
                if (!string.IsNullOrWhiteSpace(info?.Name))
                {
                    game.Name = info.Name;
                    appList.AddOrUpdate(appid, info.Name);
                }
            }
            catch { }
        }
        Fixes.Clear();
        _allFixes = [];
        FixTags.Clear();
        SelectedFixTagId = null;
        IsLoadingFixes = true;
        try
        {
            var data = await api.GetDenuvoFixesAsync(game.AppId);
            if (data is null || data.Fixes.Count == 0)
            {
                data = DataJackUIApiClient.CreateFallbackFixesResponse(game.AppId, game.Name);
            }
            else if (data.Name.StartsWith("App ", StringComparison.OrdinalIgnoreCase) && !game.HasPlaceholderName)
            {
                data.Name = game.Name;
            }
            await ProcessGameDataAsync(game, data);
        }
        catch
        {
            var fallback = DataJackUIApiClient.CreateFallbackFixesResponse(game.AppId, game.Name);
            await ProcessGameDataAsync(game, fallback);
        }
        finally { IsLoadingFixes = false; ClearCursor(); }
    }

    private async Task ProcessGameDataAsync(FixGameCardVm game, DenuvoFixesResponse data)
    {
        _allFixes = data.Fixes.Select(f => new FixItemVm(f)).ToList();
        
        long gameAppId = 0;
        long.TryParse(game.AppId, out gameAppId);
        string? installDir = gameAppId > 0 ? await Task.Run(() => library.GetInstallDir(gameAppId)) : null;
        
        foreach (var f in _allFixes)
        {
            f.GameInstalled = installDir is not null;
            f.IsApplied = installDir is not null && File.Exists(ManifestJobFactory.GetFixRecordPath(installDir, f.Id));
        }

        var distinct = _allFixes.SelectMany(f => f.Tags).GroupBy(t => t.Id).Select(g => g.First()).OrderBy(t => t.Name).ToList();
        if (distinct.Count > 1) foreach (var t in distinct) FixTags.Add(new TagPillVm(t));

        OnPropertyChanged(nameof(HasFixTags));
        ApplyFixFilter();
    }

    [RelayCommand]
    private void SelectFixTag(string? tagId)
    {
        SelectedFixTagId = SelectedFixTagId == tagId ? null : tagId;
        foreach (var pill in FixTags) pill.IsSelected = pill.Id == SelectedFixTagId;
        ApplyFixFilter();
    }

    private void ApplyFixFilter()
    {
        IEnumerable<FixItemVm> shown = _allFixes;
        if (SelectedFixTagId is { } tag) shown = shown.Where(f => f.Tags.Any(t => t.Id == tag));
        Fixes.Clear();
        foreach (var f in shown) Fixes.Add(f);
    }

    [RelayCommand]
    private void CloseDetail()
    {
        ClearCursor();
        SelectedGame = null;
    }

    // ── Downloads ────────────────────────────────────────────────────

    [RelayCommand]
    private Task DownloadManifest(FixItemVm fix) => RunDownload(fix, "manifest");

    [RelayCommand]
    private Task DownloadFix(FixItemVm fix) => RunDownload(fix, "fix");

    /// <summary>Confirm, then revert an applied fix back to its original files.</summary>
    [RelayCommand]
    private void RevertFix(FixItemVm fix)
    {
        if (SelectedGame is not { } game) return;
        _pendingRevert = (fix, game);
        ConfirmRevertTitle = string.Format(Resources.Strings.Fixes_Revert_Confirm_Title, game.Name);
        ConfirmRevertBody = string.Format(Resources.Strings.Fixes_Revert_Confirm_Body, game.Name);
        IsConfirmingRevert = true;
    }

    [RelayCommand]
    private void CancelRevertConfirm()
    {
        IsConfirmingRevert = false;
        _pendingRevert = null;
    }

    [RelayCommand]
    private async Task ConfirmRevert()
    {
        IsConfirmingRevert = false;
        var pending = _pendingRevert;
        _pendingRevert = null;
        if (pending is not (var fix, var game)) return;
        if (!long.TryParse(game.AppId, out long appId)) return;

        var result = await Task.Run(() => jobs.RevertDenuvoFix(appId, fix.Id, game.Name));

        // RevertDenuvoFix owns every revert toast (done / partial / conflict / not-found / no-record).
        // Showing another one here meant a failed revert fired two toasts for one action.
        if (result.Ok) fix.IsApplied = false;
    }

    private (FixItemVm Fix, FixGameCardVm Game)? _pendingRevert;
    [ObservableProperty] private bool _isConfirmingRevert;
    [ObservableProperty] private string _confirmRevertTitle = "";
    [ObservableProperty] private string _confirmRevertBody = "";

    /// <summary>
    /// Queue one slot of a fix. The download, install and result toast all happen in the shared queue,
    /// so this returns as soon as the item is enqueued.
    /// </summary>
    private async Task RunDownload(FixItemVm fix, string slot)
    {
        if (await PromptSignInIfGuestAsync(Resources.Strings.Fixes_SignIn)) return;
        if (SelectedGame is not { } game) return;
        long.TryParse(game.AppId, out long appId);

        EnqueueDownloadJob(fix, slot, game, appId);
    }

    private void EnqueueDownloadJob(FixItemVm fix, string slot, FixGameCardVm game, long appId)
    {
        string fallback = slot == "manifest" ? (fix.ManifestFilename ?? $"{game.AppId}.zip") : (fix.FixFilename ?? $"{game.AppId}_fix.zip");

        var job = jobs.CreateDenuvoJob(fix.Id, slot, fallback, appId, game.Name, fix.Title, onFinished: (item, result) =>
        {
            if (result is null && item.Status == DownloadStatus.Failed)
            {
                toast.Show(Resources.Strings.Fixes_Toast_DownloadFailed, item.Message ?? Resources.Strings.Fixes_Toast_DownloadFailed_Body, error: true);
                return;
            }
            if (slot == "fix" && result?.Ok == true) fix.IsApplied = true;
        });

        var item = queue.Enqueue(job);
        if (slot == "manifest") fix.ManifestItem = item;
        else fix.FixItem = item;
    }

    private async Task<bool> PromptSignInIfGuestAsync(string message)
    {
        if (!auth.IsGuest) return false;
        toast.Show(Resources.Strings.Fixes_SignInRequired, message);
        if (RequestSignIn is not null) await RequestSignIn();
        return true;
    }
}
