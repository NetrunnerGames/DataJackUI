using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DataJackUIGui.Services;
using DataJackUIGui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace DataJackUIGui.Tests;

public class BrowseSectionsTests
{
    private readonly ITestOutputHelper _output;

    public BrowseSectionsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task GetWeeklyTopSellersAsync_ReturnsTop10Games_Global_PaidOnly()
    {
        var auth = new AuthService();
        var covers = new CoverCache();
        var settings = new SettingsService();
        var cache = new CacheService();
        var appList = new SteamAppListCache();
        var appInfo = new SteamAppInfoCache(cache, appList);
        var api = new DataJackUIApiClient(auth, appInfo, appList, covers, settings);

        var list = await api.GetWeeklyTopSellersAsync();
        Assert.NotEmpty(list);
        Assert.True(list.Count <= 10);

        // Common free-to-play AppIDs that dominate raw top sellers
        long[] knownFreeAppIds = [578080, 570, 730, 1172470, 2767030, 3393110];
        long hardwareAppId = 1675200; // Steam Deck

        foreach (var item in list)
        {
            _output.WriteLine($"Weekly Top Seller: {item.AppId} - {item.Name} -> {item.CoverUrl}");
            Assert.True(item.AppId > 0);
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.NotNull(item.CoverUrl);
            Assert.Contains(item.AppId.ToString(), item.CoverUrl);

            // Must not contain free-to-play games
            Assert.DoesNotContain(item.AppId, knownFreeAppIds);

            // Must not contain hardware
            Assert.NotEqual(hardwareAppId, item.AppId);

            // Must not be delisted
            Assert.NotEqual(true, SteamDelistedHelper.CheckDelistedFast(item.AppId));
        }
    }

    [Fact]
    public async Task GetLatestMonthHitsAsync_ReturnsTopReleases_WithNamesAndCovers_PaidOnly()
    {
        var auth = new AuthService();
        var covers = new CoverCache();
        var settings = new SettingsService();
        var cache = new CacheService();
        var appList = new SteamAppListCache();
        var appInfo = new SteamAppInfoCache(cache, appList);
        var api = new DataJackUIApiClient(auth, appInfo, appList, covers, settings);

        var list = await api.GetLatestMonthHitsAsync();
        Assert.NotEmpty(list);
        Assert.True(list.Count <= 10);

        // Free-to-play releases from top releases (e.g. Aniimo 4126040, Marvel Rivals 2767030)
        long[] knownFreeAppIds = [4126040, 2767030, 578080, 570, 730];
        long hardwareAppId = 1675200;

        foreach (var item in list)
        {
            _output.WriteLine($"Latest Month Hit: {item.AppId} - {item.Name} -> {item.CoverUrl}");
            Assert.True(item.AppId > 0);
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.NotNull(item.CoverUrl);
            Assert.Contains(item.AppId.ToString(), item.CoverUrl);

            // Must not contain free-to-play games
            Assert.DoesNotContain(item.AppId, knownFreeAppIds);

            // Must not contain hardware
            Assert.NotEqual(hardwareAppId, item.AppId);

            // Must not be delisted
            Assert.NotEqual(true, SteamDelistedHelper.CheckDelistedFast(item.AppId));
        }
    }

    [Fact]
    public void AddGameCardVm_ConstructsWithAppIdAndName_ResolvesDefaultCover()
    {
        var card = new AddGameCardVm(578080, "PUBG: BATTLEGROUNDS");
        Assert.Equal(578080, card.AppId);
        Assert.Equal("PUBG: BATTLEGROUNDS", card.Name);
        Assert.Contains("578080", card.HeaderImage);
        Assert.Equal(card.HeaderImage, card.Cover);
    }

}
