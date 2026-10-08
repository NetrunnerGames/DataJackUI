using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace DataJackUIGui.Services;

/// <summary>
/// Service managing the Razorpay QR payment portal, app unlocks, and payment verification via AuthNetrunnerGames.
/// </summary>
public class PaymentService
{
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly AuthService _auth;

    private static BitmapImage? _qr100;
    private static BitmapImage? _qr200;

    public PaymentService(SettingsService settings, AuthService auth)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _settings = settings;
        _auth = auth;
    }

    /// <summary>
    /// Loads the embedded 100 INR fixed Razorpay QR image from application resources.
    /// </summary>
    public static BitmapImage GetQr100Image()
    {
        if (_qr100 is null)
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri("pack://application:,,,/Resources/QR/qr_100.png", UriKind.Absolute);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            _qr100 = img;
        }
        return _qr100;
    }

    /// <summary>
    /// Loads the embedded 200 INR fixed Razorpay QR image from application resources.
    /// </summary>
    public static BitmapImage GetQr200Image()
    {
        if (_qr200 is null)
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri("pack://application:,,,/Resources/QR/qr_200.png", UriKind.Absolute);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            _qr200 = img;
        }
        return _qr200;
    }

    /// <summary>
    /// Checks if a specific Steam App ID is already unlocked/paid for this user.
    /// </summary>
    public async Task<bool> IsAppUnlockedAsync(long appId)
    {
        // 1. Check server-granted entitlement
        if (_auth.IsAppAllowed(appId))
            return true;

        // 2. Check local persistent settings
        if (_settings.IsAppUnlocked(appId))
            return true;

        // 2. If logged in to Discord, query the AuthNetrunnerGames worker for cloud entitlement
        string? discordId = _auth.DiscordId;
        if (!string.IsNullOrEmpty(discordId))
        {
            try
            {
                var url = $"{AppConfig.ApiBaseUrl}/api/payment/check?discord_id={Uri.EscapeDataString(discordId)}&appid={appId}";
                var res = await _http.GetAsync(url);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
                    if (doc.RootElement.TryGetProperty("unlocked", out var unProp) && unProp.GetBoolean())
                    {
                        _settings.UnlockApp(appId);
                        return true;
                    }
                }
            }
            catch
            {
                // Network unavailable: fall back to local settings
            }
        }

        return false;
    }

    /// <summary>
    /// Verifies the 12-digit UPI Reference / UTR number against the AuthNetrunnerGames Cloudflare Worker.
    /// </summary>
    public async Task<(bool Success, string? Error)> VerifyPaymentAsync(long appId, string utr, int amount, bool isDenuvo)
    {
        string cleanUtr = utr.Trim();
        if (cleanUtr.Length != 12 || !long.TryParse(cleanUtr, out _))
        {
            return (false, "Please enter a valid 12-digit UPI Reference / UTR number.");
        }

        string? discordId = _auth.DiscordId;

        try
        {
            var payload = new
            {
                discord_id = discordId,
                appid = appId,
                utr = cleanUtr,
                amount = amount,
                is_denuvo = isDenuvo
            };

            var res = await _http.PostAsJsonAsync($"{AppConfig.ApiBaseUrl}/api/payment/verify", payload);
            if (!res.IsSuccessStatusCode)
            {
                var errStr = await res.Content.ReadAsStringAsync();
                try
                {
                    using var doc = JsonDocument.Parse(errStr);
                    if (doc.RootElement.TryGetProperty("error", out var eProp))
                        return (false, eProp.GetString() ?? "Payment verification failed.");
                }
                catch { }
                return (false, "Payment verification failed. Check your UTR number and try again.");
            }

            // Successfully verified! Save to local settings
            _settings.UnlockApp(appId);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Network error: {ex.Message}");
        }
    }
}
