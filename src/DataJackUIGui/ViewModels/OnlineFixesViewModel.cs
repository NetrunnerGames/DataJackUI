using DataJackUIGui.Services;
using DataJackUIGui.Services.Downloads;

namespace DataJackUIGui.ViewModels;

public class OnlineFixesViewModel : FixesViewModel
{
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
    }

    public virtual string HeaderTitle => Resources.Strings.Nav_OnlineFix;

    protected override List<FixGameCardVm> FilterLoadedGames(List<FixGameCardVm> all) =>
        all.Where(g => g.MatchesTag("online") ||
                       (!string.IsNullOrEmpty(g.AppId) && g.AppId.StartsWith("of_", StringComparison.OrdinalIgnoreCase)) ||
                       (g.OnlineUrl != null && g.OnlineUrl.Contains("online-fix"))).ToList();
}
