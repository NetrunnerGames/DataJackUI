using DataJackUIGui.Services;
using Xunit;

namespace DataJackUIGui.Tests;

public class PaymentServiceTests
{
    [Fact]
    public void SettingsService_Tracks_UnlockedAppIds()
    {
        var settings = new SettingsService();
        long testAppId = 99999991;
        settings.RelockApp(testAppId);

        Assert.False(settings.IsAppUnlocked(testAppId));

        settings.UnlockApp(testAppId);
        Assert.True(settings.IsAppUnlocked(testAppId));

        settings.RelockApp(testAppId);
    }

    [Theory]
    [InlineData("123456789012", true)]
    [InlineData("428109283719", true)]
    [InlineData("12345", false)]
    [InlineData("12345678901a", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("12345678901234", false)]
    public async Task PaymentService_Validates_Utr_Format(string utr, bool expectedValid)
    {
        var settings = new SettingsService();
        var auth = new AuthService();
        var payment = new PaymentService(settings, auth);

        var (success, error) = await payment.VerifyPaymentAsync(730, utr, 100, isDenuvo: false);

        if (!expectedValid)
        {
            Assert.False(success);
            Assert.Contains("12-digit", error);
        }
    }

    [Theory]
    [InlineData(1687950, true)]  // Persona 5 Royal (Active Denuvo)
    [InlineData(2161700, true)]  // Persona 3 Reload (Active Denuvo)
    [InlineData(2358720, true)]  // Black Myth: Wukong (Active Denuvo)
    [InlineData(1237320, true)]  // Sonic Frontiers (Active Denuvo)
    [InlineData(990080, true)]   // Hogwarts Legacy (Active Denuvo)
    [InlineData(730, false)]     // Counter-Strike 2 (Never had Denuvo)
    [InlineData(1478500, false)] // Big Walk (Never had Denuvo)
    [InlineData(208650, false)]  // Batman: Arkham Knight (Denuvo Removed)
    [InlineData(397540, false)]  // Borderlands 3 (Denuvo Removed)
    [InlineData(1196590, false)] // Resident Evil Village (Denuvo Removed)
    [InlineData(668580, false)]  // Atomic Heart (Denuvo Removed)
    public void DenuvoService_Identifies_Baseline_Titles(long appId, bool expectedDenuvo)
    {
        var cache = new CacheService();
        var denuvo = new DenuvoService(cache);

        Assert.Equal(expectedDenuvo, denuvo.IsDenuvo(appId));
    }

    [Fact]
    public void GameDetails_Detects_Denuvo_From_Steam_DrmNotice()
    {
        var game = new Models.GameDetails
        {
            AppId = 12345,
            Name = "Test Game",
            DrmNotice = "Incorporates 3rd-party DRM: Denuvo Anti-tamper"
        };
        Assert.True(game.HasDenuvo);

        var normalGame = new Models.GameDetails
        {
            AppId = 54321,
            Name = "Indie Game",
            DrmNotice = null
        };
        Assert.False(normalGame.HasDenuvo);
    }
}
