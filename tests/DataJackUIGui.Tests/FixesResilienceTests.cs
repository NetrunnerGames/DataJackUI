using DataJackUIGui.Services;
using Xunit;

namespace DataJackUIGui.Tests;

public class FixesResilienceTests
{
    [Fact]
    public void BaselineActiveIds_Contains_Verified_Denuvo_Games()
    {
        Assert.NotEmpty(DenuvoService.BaselineActiveIds);
        Assert.True(DenuvoService.BaselineActiveIds.Count > 200, "Should contain comprehensive baseline of verified games");

        // Verify notable active titles exist in baseline
        Assert.Contains(234140, DenuvoService.BaselineActiveIds);  // Mad Max
        Assert.Contains(1687950, DenuvoService.BaselineActiveIds); // Persona 5 Royal
        Assert.Contains(2161700, DenuvoService.BaselineActiveIds); // Persona 3 Reload
        Assert.Contains(2358720, DenuvoService.BaselineActiveIds); // Black Myth: Wukong
    }

    [Fact]
    public void CreateFallbackFixesResponse_Generates_Valid_Fix_Structure()
    {
        string appId = "234140";
        string gameName = "Mad Max";

        var response = DataJackUIApiClient.CreateFallbackFixesResponse(appId, gameName);

        Assert.NotNull(response);
        Assert.Equal(appId, response.AppId);
        Assert.Equal(gameName, response.Name);
        Assert.NotEmpty(response.Fixes);

        var firstFix = response.Fixes[0];
        Assert.Equal("234140_bypass", firstFix.Id);
        Assert.True(firstFix.HasManifest);
        Assert.True(firstFix.HasFix);
        Assert.Equal("234140.zip", firstFix.ManifestFilename);
        Assert.Equal("Mad_Max_bypass.zip", firstFix.FixFilename);
        Assert.Contains(firstFix.Tags, t => t.Id == "bypass");
    }

    [Theory]
    [InlineData("234140_bypass", 234140)]
    [InlineData("1687950", 1687950)]
    [InlineData("2161700_online", 2161700)]
    public void FixId_Numeric_Parsing_Extracts_Correct_AppId(string fixId, long expectedAppId)
    {
        long.TryParse(fixId.Split('_')[0], out long parsedAppId);
        Assert.Equal(expectedAppId, parsedAppId);
    }

    [Fact]
    public void FixTypeLabel_Formats_Fix_Kinds_Properly()
    {
        var listing = new DataJackUIGui.Models.DenuvoGameListing
        {
            AppId = "234140",
            Name = "Mad Max",
            Tags = new List<DataJackUIGui.Models.DenuvoTag>
            {
                new() { Id = "bypass", Name = "Bypass", Slug = "bypass" }
            }
        };

        var vm = new DataJackUIGui.ViewModels.FixGameCardVm(listing);
        Assert.Equal("Bypass", vm.FixTypeLabel);
        Assert.Equal("Bypass", vm.FixCountLabel);
        Assert.True(vm.MatchesTag("bypass"));
        Assert.False(vm.MatchesTag("online"));
        Assert.False(vm.MatchesTag("hypervisor"));
    }

    [Fact]
    public void FixTypeLabel_Online_Kind_Formatted_Cleanly()
    {
        var listing = new DataJackUIGui.Models.DenuvoGameListing
        {
            AppId = "of_17429",
            Name = "Palworld",
            Tags = new List<DataJackUIGui.Models.DenuvoTag>
            {
                new() { Id = "online", Name = "Online Fix", Slug = "online" }
            }
        };

        var vm = new DataJackUIGui.ViewModels.FixGameCardVm(listing);
        Assert.Equal("Online Fix", vm.FixTypeLabel);
        Assert.True(vm.MatchesTag("online"));
        Assert.False(vm.MatchesTag("bypass"));
    }

    [Fact]
    public void FixGameCardVm_Title_With_Online_Word_Does_Not_Match_Online_Tag_Unless_Explicitly_Tagged()
    {
        var listing = new DataJackUIGui.Models.DenuvoGameListing
        {
            AppId = "607890",
            Name = "Sword Art Online: Fatal Bullet",
            Tags = new List<DataJackUIGui.Models.DenuvoTag>
            {
                new() { Id = "bypass", Name = "Bypass", Slug = "bypass" }
            }
        };

        var vm = new DataJackUIGui.ViewModels.FixGameCardVm(listing);
        Assert.Equal("Bypass", vm.FixTypeLabel);
        Assert.True(vm.MatchesTag("bypass"));
        Assert.False(vm.MatchesTag("online"));
    }

    [Fact]
    public void FixGameCardVm_PlaceholderName_DetectedProperly()
    {
        var placeholderListing = new DataJackUIGui.Models.DenuvoGameListing
        {
            AppId = "1963680",
            Name = "App 1963680",
        };
        var vm = new DataJackUIGui.ViewModels.FixGameCardVm(placeholderListing);
        Assert.True(vm.HasPlaceholderName);

        var realListing = new DataJackUIGui.Models.DenuvoGameListing
        {
            AppId = "1963680",
            Name = "GUNDAM ROGUE ORBIT",
        };
        var vmReal = new DataJackUIGui.ViewModels.FixGameCardVm(realListing);
        Assert.False(vmReal.HasPlaceholderName);
    }

    [Fact]
    public async Task FixGameCardVm_EnsureCoverAsync_Updates_Name_From_AppList()
    {
        var listing = new DataJackUIGui.Models.DenuvoGameListing
        {
            AppId = "1963680",
            Name = "App 1963680",
        };
        var vm = new DataJackUIGui.ViewModels.FixGameCardVm(listing);
        Assert.True(vm.HasPlaceholderName);

        var appList = new SteamAppListCache();
        appList.AddOrUpdate(1963680, "GUNDAM ROGUE ORBIT");

        var cache = new CacheService();
        var appInfo = new SteamAppInfoCache(cache, appList);
        var covers = new CoverCache();

        await vm.EnsureCoverAsync(covers, appInfo, appList);

        Assert.Equal("GUNDAM ROGUE ORBIT", vm.Name);
        Assert.False(vm.HasPlaceholderName);
    }
}
