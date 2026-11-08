using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataJackUIGui.Models;
using DataJackUIGui.Services;
using DataJackUIGui.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataJackUIGui.Tests;

[Collection("AuthFile")]
public class OnboardingAuthTests : IDisposable
{
    private readonly string _tempFile;
    private readonly string _originalAuthFile;
    private readonly AuthService _auth;

    public OnboardingAuthTests()
    {
        _originalAuthFile = AuthService.AuthFile;
        _tempFile = Path.Combine(Path.GetTempPath(), $"datajackui_test_auth_{Guid.NewGuid():N}.dat");
        AuthService.AuthFile = _tempFile;
        _auth = new AuthService();
    }

    public void Dispose()
    {
        AuthService.AuthFile = _originalAuthFile;
        if (File.Exists(_tempFile))
        {
            try { File.Delete(_tempFile); } catch { }
        }
    }

    [Fact]
    public async Task AuthService_GoogleUser_BypassesDiscordGuildCheck()
    {
        // Save stored Google session (DiscordId is null/empty)
        var googleAuth = new StoredAuth
        {
            AccessToken = "google_access_token_123",
            RefreshToken = "google_refresh_token_456",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Username = "GooglePlayer",
            UserId = "google_user_789",
            AvatarUrl = "https://lh3.googleusercontent.com/avatar.png"
        };
        AuthService.SaveStored(googleAuth);

        bool restored = await _auth.InitializeAsync();
        Assert.True(restored);
        Assert.True(_auth.IsSignedIn);
        Assert.Null(_auth.DiscordId);

        // VerifyServerMembershipAsync should return true for Google accounts
        bool valid = await _auth.VerifyServerMembershipAsync();
        Assert.True(valid);
    }

    [Fact]
    public async Task OnboardingViewModel_SetsWaitingMessageForGoogleAndDiscord()
    {
        var cache = new CacheService();
        var settings = new SettingsService();
        var steam = new SteamService(settings);
        var gh = new GithubProxy();
        var unlocker = new UnlockerService(steam, settings, cache, gh);
        var cef = new CefInjectorService(steam, NullLogger<CefInjectorService>.Instance);
        var toast = new ToastService();
        var installer = new PluginInstallerService(steam, gh, cef, unlocker);

        var vm = new OnboardingViewModel(_auth, cache, settings, unlocker, steam, installer, toast);

        Assert.Equal("Waiting for authorization...", vm.WaitingMessage);

        // Start Google sign-in first: launches authorization URL in external default browser
        var googleTask = vm.SignInGoogleCommand.ExecuteAsync(null);
        Assert.Equal("Waiting for Google...", vm.WaitingMessage);
        Assert.True(vm.IsSigningIn);

        // Keep the browser session alive for 4 seconds so the window/tab is visibly opened
        await Task.Delay(4000);

        // Cancel Google sign-in and await task completion
        vm.CancelSignIn();
        await googleTask;
        Assert.False(vm.IsSigningIn);

        // Next test Discord sign-in
        var discordTask = vm.SignInCommand.ExecuteAsync(null);
        Assert.Equal("Waiting for Discord...", vm.WaitingMessage);
        await Task.Delay(500);
        vm.CancelSignIn();
        await discordTask;
        Assert.False(vm.IsSigningIn);
    }

    [Fact]
    public async Task AuthService_GoogleUser_SetsEntitlementsAndUserIdentity()
    {
        // Simulate Google authenticated state
        var googleAuth = new StoredAuth
        {
            AccessToken = "google_access_token_123",
            RefreshToken = "google_refresh_token_456",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Username = "GooglePlayer",
            UserId = "google_user_789",
            AvatarUrl = "https://lh3.googleusercontent.com/avatar.png"
        };
        AuthService.SaveStored(googleAuth);

        bool restored = await _auth.InitializeAsync();
        Assert.True(restored);

        Assert.NotNull(_auth.CurrentEntitlement);
        Assert.Equal("GooglePlayer", _auth.Username);
        Assert.Equal("google_user_789", _auth.UserId);
        Assert.Equal("google_user_789", _auth.CurrentEntitlement.DiscordId);
    }
}
