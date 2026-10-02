using System.IO;
using System.Linq;
using DataJackUIGui.Models;
using DataJackUIGui.Services;
using DataJackUIGui.ViewModels;
using Xunit;

namespace DataJackUIGui.Tests;

public class TaskRequirementsTests
{
    [Fact]
    public void SettingsService_DonateKeys_DefaultsToFalse()
    {
        var settings = new SettingsService();
        Assert.False(settings.DonateKeys);
    }

    [Fact]
    public void SettingsViewModel_ExposesUsernameAndUserId()
    {
        var settings = new SettingsService();
        var auth = new AuthService();
        var steam = new SteamService(settings);
        var hubcap = new HubcapService();

        var vm = new SettingsViewModel(settings, auth, steam, hubcap);

        Assert.Equal(auth.Username ?? auth.DisplayName, vm.Username);
        Assert.Equal(auth.UserId ?? auth.DiscordId, vm.UserId);
    }

    [Fact]
    public void SettingsViewModel_LanguageOptions_ReflectsSystemDefaultAndEnglish()
    {
        var settings = new SettingsService();
        var auth = new AuthService();
        var steam = new SteamService(settings);
        var hubcap = new HubcapService();

        var vm = new SettingsViewModel(settings, auth, steam, hubcap);

        Assert.Equal(2, vm.LanguageOptions.Count);
        Assert.Null(vm.LanguageOptions[0].Tag);
        Assert.Equal("en", vm.LanguageOptions[1].Tag);
    }

    [Fact]
    public void SettingsViewModel_ToolsPath_BindsToSettingsService()
    {
        var settings = new SettingsService();
        var auth = new AuthService();
        var steam = new SteamService(settings);
        var hubcap = new HubcapService();

        var vm = new SettingsViewModel(settings, auth, steam, hubcap);
        vm.ToolsPath = @"C:\CustomToolsPath";

        Assert.Equal(@"C:\CustomToolsPath", settings.ToolsPath);
    }

    [Fact]
    public void ResourcesDirectory_ContainsOnlyEnglishResx()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        // Locate src/DataJackUIGui/Resources from output directory
        string resDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "DataJackUIGui", "Resources"));
        if (Directory.Exists(resDir))
        {
            var resxFiles = Directory.GetFiles(resDir, "*.resx")
                .Select(Path.GetFileName)
                .ToList();

            Assert.Single(resxFiles);
            Assert.Equal("Strings.resx", resxFiles[0]);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task SearchGames_DoesNotCrash()
    {
        var settings = new SettingsService();
        var cache = new CacheService();
        var auth = new AuthService();
        var appList = new SteamAppListCache();
        var appInfo = new SteamAppInfoCache(cache);
        var covers = new CoverCache(settings);
        var client = new DataJackUIApiClient(auth, appInfo, appList, covers, settings);

        var results = await client.SearchAsync("grand theft auto v");
        Assert.NotNull(results);
    }

    [Fact]
    public async System.Threading.Tasks.Task SearchGames_IncludesLegacyAndDelistedAppIds()
    {
        var settings = new SettingsService();
        var cache = new CacheService();
        var auth = new AuthService();
        var appList = new SteamAppListCache();
        appList.AddOrUpdate(271590, "Grand Theft Auto V");
        appList.AddOrUpdate(3240220, "Grand Theft Auto V (Enhanced)");

        var appInfo = new SteamAppInfoCache(cache);
        var covers = new CoverCache(settings);
        var client = new DataJackUIApiClient(auth, appInfo, appList, covers, settings);

        var results = await client.SearchAsync("Grand Theft Auto V");
        Assert.NotNull(results);
        Assert.Contains(results, r => r.AppId == 271590);
    }
}
