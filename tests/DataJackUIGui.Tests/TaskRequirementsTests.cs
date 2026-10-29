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
    public async Task HomeViewModel_AccountStatus_DisplaysCustomUsernameOrAnonymous()
    {
        string authBackup = AuthService.AuthFile + ".bak_test";
        if (File.Exists(AuthService.AuthFile)) File.Copy(AuthService.AuthFile, authBackup, true);

        try
        {
            var stored = new StoredAuth
            {
                AccessToken = "test-token",
                RefreshToken = "test-refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
                Username = "TestUser",
                UserId = "user-123"
            };
            AuthService.SaveStored(stored);

            var settings = new SettingsService();
            settings.CustomUsername = null;
            var auth = new AuthService();

            var steam = new SteamService(settings);
            var cache = new CacheService();
            var appList = new SteamAppListCache();
            var appInfo = new SteamAppInfoCache(cache, appList);
            var covers = new CoverCache(settings);
            var vault = new LuaVault(steam);
            var installer = new LuaInstaller(steam, settings, cache, vault);
            var depotInfo = new SteamDepotInfo();
            var drop = new DropInstallViewModel(installer, appList, appInfo, depotInfo);
            var gh = new GithubProxy();
            var unlocker = new UnlockerService(steam, settings, cache, gh);
            var injector = new CefInjectorService(steam, Microsoft.Extensions.Logging.Abstractions.NullLogger<CefInjectorService>.Instance);
            var plugin = new PluginInstallerService(steam, gh, injector, unlocker);
            var toast = new ToastService();

            var vm = new HomeViewModel(steam, auth, settings, appList, appInfo, covers, drop, unlocker, plugin, toast);
            await auth.InitializeAsync();

            // When signed in and no custom name is set, displays anonymous
            Assert.Equal("Signed in as anonymous", vm.AccountStatus);

            // When custom name is set in settings, immediately reflects it
            settings.CustomUsername = "internetninja";
            Assert.Equal("Signed in as internetninja", vm.AccountStatus);

            // When cleared back to null/whitespace, displays anonymous
            settings.CustomUsername = "   ";
            Assert.Equal("Signed in as anonymous", vm.AccountStatus);

            // Cleanup
            settings.CustomUsername = null;
        }
        finally
        {
            try
            {
                if (File.Exists(authBackup))
                {
                    File.Copy(authBackup, AuthService.AuthFile, true);
                    File.Delete(authBackup);
                }
                else
                {
                    File.Delete(AuthService.AuthFile);
                }
            }
            catch { }
        }
    }

    [Fact]
    public void SettingsService_Profile_PersistsAcrossCleanInstallViaRegistry()
    {
        var settings = new SettingsService();
        settings.CustomUsername = "PersistedRunner";
        
        // Mock a clean install / reinstall where settings.json is deleted
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUIGui");
        string settingsPath = Path.Combine(dir, "settings.json");
        string bakPath = settingsPath + ".bak";
        
        string? savedJson = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        try
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (File.Exists(bakPath)) File.Delete(bakPath);

            // Re-instantiate SettingsService as on a fresh install / update
            var newSettings = new SettingsService();

            // Username should be restored from HKCU\Software\DataJackUI
            Assert.Equal("PersistedRunner", newSettings.CustomUsername);
        }
        finally
        {
            // Restore original settings
            if (savedJson is not null)
            {
                File.WriteAllText(settingsPath, savedJson);
            }
            settings.CustomUsername = null;
        }
    }

    [Fact]
    public void SettingsViewModel_ExposesUsernameAndUserId()
    {
        var settings = new SettingsService();
        settings.CustomUsername = null;
        var auth = new AuthService();
        var steam = new SteamService(settings);
        var hubcap = new HubcapService();
        var update = new UpdateService();

        var vm = new SettingsViewModel(settings, auth, steam, hubcap, update);

        Assert.Equal("anonymous", vm.Username);
        Assert.Equal(auth.UserId ?? auth.DiscordId, vm.UserId);
    }

    [Fact]
    public void SettingsViewModel_CustomUsername_UpdatesAndPersists()
    {
        var settings = new SettingsService();
        settings.CustomUsername = null;
        var auth = new AuthService();
        var steam = new SteamService(settings);
        var hubcap = new HubcapService();
        var update = new UpdateService();

        var vm = new SettingsViewModel(settings, auth, steam, hubcap, update);

        Assert.Equal("anonymous", vm.Username);
        vm.Username = "Netrunner99";
        Assert.Equal("Netrunner99", settings.CustomUsername);
        Assert.Equal("Netrunner99", vm.Username);

        vm.Username = "";
        Assert.Equal("anonymous", settings.CustomUsername);

        settings.CustomUsername = null;
    }

    [Fact]
    public void SettingsViewModel_CustomAvatarUrl_AppliesAndResets()
    {
        var settings = new SettingsService();
        var auth = new AuthService();
        var steam = new SteamService(settings);
        var hubcap = new HubcapService();
        var update = new UpdateService();

        var vm = new SettingsViewModel(settings, auth, steam, hubcap, update);

        Assert.False(vm.IsAvatarDialogOpen);
        vm.OpenAvatarDialogCommand.Execute(null);
        Assert.True(vm.IsAvatarDialogOpen);

        vm.AvatarUrlInput = "https://example.com/pfp.png";
        vm.ApplyAvatarUrlCommand.Execute(null);

        Assert.False(vm.IsAvatarDialogOpen);
        Assert.True(vm.HasCustomAvatar);
        Assert.Equal("https://example.com/pfp.png", settings.CustomAvatarPath);

        vm.ResetAvatarCommand.Execute(null);
        Assert.False(vm.HasCustomAvatar);
        Assert.Null(settings.CustomAvatarPath);
    }

    [Fact]
    public void SettingsViewModel_ProfileEditing_TogglesAndResetsEmptyUsername()
    {
        var settings = new SettingsService();
        settings.CustomUsername = null;
        var auth = new AuthService();
        var steam = new SteamService(settings);
        var hubcap = new HubcapService();
        var update = new UpdateService();

        var vm = new SettingsViewModel(settings, auth, steam, hubcap, update);

        Assert.False(vm.IsEditingProfile);

        vm.StartEditProfileCommand.Execute(null);
        Assert.True(vm.IsEditingProfile);

        vm.Username = "   ";
        vm.FinishEditProfileCommand.Execute(null);
        Assert.False(vm.IsEditingProfile);
        Assert.Equal("anonymous", vm.Username);

        vm.ToggleEditProfileCommand.Execute(null);
        Assert.True(vm.IsEditingProfile);
        vm.Username = " CyberDeck ";
        vm.ToggleEditProfileCommand.Execute(null);
        Assert.False(vm.IsEditingProfile);
        Assert.Equal("CyberDeck", vm.Username);

        settings.CustomUsername = null;
    }

    [Fact]
    public void SettingsViewModel_LanguageOptions_ReflectsSystemDefaultAndEnglish()
    {
        var settings = new SettingsService();
        var auth = new AuthService();
        var steam = new SteamService(settings);
        var hubcap = new HubcapService();
        var update = new UpdateService();

        var vm = new SettingsViewModel(settings, auth, steam, hubcap, update);

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
        var update = new UpdateService();

        var vm = new SettingsViewModel(settings, auth, steam, hubcap, update);
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

    [Fact]
    public async Task PaymentService_CheckAuthToken_In_Verify()
    {
        var settings = new SettingsService();
        var auth = new AuthService();
        await auth.InitializeAsync();
        var payment = new PaymentService(settings, auth);

        var (success, error) = await payment.VerifyPaymentAsync(271590, "665767895987", 100, isDenuvo: false);
        if (!auth.IsSignedIn)
        {
            Assert.False(success);
            Assert.Contains("Valid login required", error);
        }
        else
        {
            // If signed in, server evaluates the order ID
            Assert.NotNull(error ?? (success ? "ok" : null));
        }
    }
}
