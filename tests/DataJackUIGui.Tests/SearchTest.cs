using DataJackUIGui.Services;
using DataJackUIGui.Services.Downloads;
using Xunit;
using Xunit.Abstractions;

namespace DataJackUIGui.Tests;

public class SearchTest
{
    private readonly ITestOutputHelper _output;

    public SearchTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task SearchAsync_ReturnsResultsForCyber()
    {
        var auth = new AuthService();
        var covers = new CoverCache();
        var settings = new SettingsService();
        var cache = new CacheService();
        var appList = new SteamAppListCache();
        var appInfo = new SteamAppInfoCache(cache, appList);
        var api = new DataJackUIApiClient(auth, appInfo, appList, covers, settings);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _output.WriteLine("Calling EnsureLoadedAsync...");
        await appList.EnsureLoadedAsync();
        _output.WriteLine($"EnsureLoadedAsync took {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var localResults = appList.Search("cyber");
        _output.WriteLine($"Local search found {localResults.Count} results in {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var results = await api.SearchAsync("cyber");
        _output.WriteLine($"SearchAsync returned {results.Count} results in {sw.ElapsedMilliseconds} ms");
        foreach (var r in results)
        {
            _output.WriteLine($"AppId: {r.AppId}, Name: {r.Name}");
        }

        Assert.NotEmpty(results);
    }
}
