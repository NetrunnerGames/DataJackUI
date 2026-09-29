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
}
