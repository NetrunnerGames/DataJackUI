using System;
using System.Threading.Tasks;
using DataJackUIGui.Services;
using Xunit;

namespace DataJackUIGui.Tests;

public class AutoUpdateTests
{
    [Fact]
    public void UpdateService_DefaultPeriodicCheckInterval_IsSixHours()
    {
        using var updateService = new UpdateService();
        Assert.Equal(TimeSpan.FromHours(6), updateService.PeriodicCheckInterval);
    }

    [Fact]
    public void UpdateService_StartAndStopPeriodicChecks_TogglesState()
    {
        using var updateService = new UpdateService();
        Assert.False(updateService.IsPeriodicCheckRunning);

        updateService.StartPeriodicChecks(TimeSpan.FromMinutes(10));
        Assert.True(updateService.IsPeriodicCheckRunning);
        Assert.Equal(TimeSpan.FromMinutes(10), updateService.PeriodicCheckInterval);

        updateService.StopPeriodicChecks();
        Assert.False(updateService.IsPeriodicCheckRunning);
    }

    [Fact]
    public async Task UpdateService_WhenUpdateFound_PromptsUser_AndIfConfirmed_DownloadsAndApplies()
    {
        using var updateService = new UpdateService();
        bool prompted = false;
        string? promptedVersion = null;
        bool downloaded = false;
        string? downloadedVersion = null;
        bool applied = false;
        bool updateReadyFired = false;

        updateService.CustomCheckForUpdatesHandler = () => Task.FromResult<string?>("2.12.7");
        updateService.PromptUserForUpdate = version =>
        {
            prompted = true;
            promptedVersion = version;
            return Task.FromResult(true); // User selects "Update Now"
        };
        updateService.CustomDownloadUpdatesHandler = version =>
        {
            downloaded = true;
            downloadedVersion = version;
            return Task.CompletedTask;
        };
        updateService.CustomApplyUpdatesHandler = _ =>
        {
            applied = true;
        };
        updateService.UpdateReady += () =>
        {
            updateReadyFired = true;
        };

        bool result = await updateService.CheckForUpdatesAndPromptAsync();

        Assert.True(result);
        Assert.True(prompted);
        Assert.Equal("2.12.7", promptedVersion);
        Assert.True(downloaded);
        Assert.Equal("2.12.7", downloadedVersion);
        Assert.True(applied);
        Assert.True(updateReadyFired);
    }

    [Fact]
    public async Task UpdateService_WhenUpdateFound_PromptsUser_AndIfDeclined_StagesWithoutRestart()
    {
        using var updateService = new UpdateService();
        bool prompted = false;
        bool downloaded = false;
        bool applied = false;

        updateService.CustomCheckForUpdatesHandler = () => Task.FromResult<string?>("2.12.7");
        updateService.PromptUserForUpdate = version =>
        {
            prompted = true;
            return Task.FromResult(false); // User selects "Later"
        };
        updateService.CustomDownloadUpdatesHandler = version =>
        {
            downloaded = true;
            return Task.CompletedTask;
        };
        updateService.CustomApplyUpdatesHandler = _ =>
        {
            applied = true;
        };

        bool result = await updateService.CheckForUpdatesAndPromptAsync();

        Assert.False(result);
        Assert.True(prompted);
        Assert.True(downloaded);
        Assert.False(applied); // No restart forced when "Later" is chosen
    }

    [Fact]
    public async Task UpdateService_WhenNoUpdateFound_DoesNotPromptOrDownload()
    {
        using var updateService = new UpdateService();
        bool prompted = false;
        bool downloaded = false;

        updateService.CustomCheckForUpdatesHandler = () => Task.FromResult<string?>(null);
        updateService.PromptUserForUpdate = version =>
        {
            prompted = true;
            return Task.FromResult(true);
        };
        updateService.CustomDownloadUpdatesHandler = version =>
        {
            downloaded = true;
            return Task.CompletedTask;
        };

        bool result = await updateService.CheckForUpdatesAndPromptAsync();

        Assert.False(result);
        Assert.False(prompted);
        Assert.False(downloaded);
    }

    [Fact]
    public void UpdateService_Dispose_StopsPeriodicChecks()
    {
        var updateService = new UpdateService();
        updateService.StartPeriodicChecks(TimeSpan.FromHours(1));
        updateService.Dispose();
        Assert.False(updateService.IsPeriodicCheckRunning);
    }

    [Fact]
    public async Task UpdateService_CheckAndAutoUpdateAsync_DownloadsAndStagesSilentlyWithoutPromptOrRestart()
    {
        using var updateService = new UpdateService();
        bool downloaded = false;
        string? downloadedVersion = null;
        bool updateReadyFired = false;

        updateService.CustomCheckForUpdatesHandler = () => Task.FromResult<string?>("2.13.0");
        updateService.CustomDownloadUpdatesHandler = version =>
        {
            downloaded = true;
            downloadedVersion = version;
            return Task.CompletedTask;
        };
        updateService.UpdateReady += () =>
        {
            updateReadyFired = true;
        };

        var result = await updateService.CheckAndAutoUpdateAsync();

        Assert.True(downloaded);
        Assert.Equal("2.13.0", downloadedVersion);
        Assert.True(updateReadyFired);
    }

    [Fact]
    public void AppConfig_VelopackReleaseFeedUrl_PointsToDirectStaticReleaseAssets()
    {
        Assert.Equal("https://github.com/NetrunnerGames/DataJackUI/releases/latest/download", AppConfig.VelopackReleaseFeedUrl);
        Assert.DoesNotContain("api.github.com", AppConfig.VelopackReleaseFeedUrl);
    }
}
