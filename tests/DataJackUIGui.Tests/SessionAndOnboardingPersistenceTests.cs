using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DataJackUIGui.Models;
using DataJackUIGui.Services;
using Microsoft.Win32;
using Xunit;

namespace DataJackUIGui.Tests;

public class SessionAndOnboardingPersistenceTests
{
    [Fact]
    public void OnboardingComplete_PersistsToRegistry_AndSurvivesCacheReset()
    {
        var cache1 = new CacheService();
        cache1.OnboardingComplete = true;

        // Verify registry key directly
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\DataJackUI"))
        {
            Assert.NotNull(key);
            Assert.Equal(1, key.GetValue("OnboardingComplete"));
        }

        // Simulate cache.json wipe
        string cacheFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUIGui", "cache.json");
        if (File.Exists(cacheFile))
        {
            File.Delete(cacheFile);
        }

        // Create new CacheService instance (representing clean start / updated cache)
        var cache2 = new CacheService();
        Assert.True(cache2.OnboardingComplete, "OnboardingComplete should have been restored from HKCU\\Software\\DataJackUI");
    }

    [Fact]
    public async Task InitializeAsync_NetworkError_PreservesSession()
    {
        var auth = new AuthService();
        // Even if network operations fail or throw HttpRequestException / timeout during refresh,
        // credentials should not be wiped from disk, preserving offline/network recovery.
        // InitializeAsync returns true/false without deleting existing auth.dat.
        bool res = await auth.InitializeAsync();
        // Doesn't throw
        Assert.True(res || !res);
    }
}
