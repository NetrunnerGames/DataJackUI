using DataJackUIGui.Models;
using DataJackUIGui.Services;
using DataJackUIGui.Services.Downloads;

namespace DataJackUIGui.ViewModels;

public class OnlineFixesViewModel : FixesViewModel
{
    private readonly DataJackUIApiClient _api;
    private readonly CoverCache _covers;
    private readonly SteamService _steam;

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
        _covers = covers;
        _steam = steam;
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

            _allGames = data.Games.Select(g => new FixGameCardVm(g, _covers)).ToList();
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
}
