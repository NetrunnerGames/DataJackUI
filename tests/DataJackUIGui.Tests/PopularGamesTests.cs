using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DataJackUIGui.Services;
using Xunit;
using Xunit.Abstractions;

namespace DataJackUIGui.Tests;

public class PopularGamesTests
{
    private readonly ITestOutputHelper _output;

    public PopularGamesTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task GetPopularGamesAsync_ExcludesPubg_AndResolvesHashedHeroes()
    {
        var auth = new AuthService();
        var covers = new CoverCache();
        var settings = new SettingsService();
        var cache = new CacheService();
        var appList = new SteamAppListCache();
        var appInfo = new SteamAppInfoCache(cache, appList);
        var api = new DataJackUIApiClient(auth, appInfo, appList, covers, settings);

        var list = await api.GetPopularGamesAsync();
        Assert.NotEmpty(list);

        // PUBG (578080) must be excluded
        Assert.DoesNotContain(list, g => g.AppId == 578080);

        foreach (var g in list)
        {
            _output.WriteLine($"Rank {g.Rank}: {g.AppId} - {g.Name} -> {g.HeroImage}");
            Assert.False(string.IsNullOrWhiteSpace(g.HeroImage));
        }

        // Check that WARDOGS or AION 2, if in the top list, have hashed hero URLs
        var wardogs = list.FirstOrDefault(g => g.AppId == 1867240);
        if (wardogs != null)
        {
            Assert.Contains("/9e3d0dba457f3d33734990866160be80c399f67c/", wardogs.HeroImage);
        }

        var aion2 = list.FirstOrDefault(g => g.AppId == 3393110);
        if (aion2 != null)
        {
            Assert.Contains("/0f3eb5d09fe82771cc01c87cd6d12fb1f38d6069/", aion2.HeroImage);
        }
    }

    [Fact]
    public async Task EnsureHeroAsync_ResolvesHashedHeroForWardogsAndAion2()
    {
        var covers = new CoverCache();

        // Pass old/unhashed 404 URL for WARDOGS (1867240) to verify fallback resolution
        string? wardogsPath = await covers.EnsureHeroAsync(1867240, "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1867240/library_hero.jpg");
        Assert.NotNull(wardogsPath);
        Assert.True(File.Exists(wardogsPath));
        Assert.True(new FileInfo(wardogsPath).Length > 10000);

        // Pass null or unhashed URL for AION 2 (3393110)
        string? aion2Path = await covers.EnsureHeroAsync(3393110, null);
        Assert.NotNull(aion2Path);
        Assert.True(File.Exists(aion2Path));
        Assert.True(new FileInfo(aion2Path).Length > 10000);
    }
}
