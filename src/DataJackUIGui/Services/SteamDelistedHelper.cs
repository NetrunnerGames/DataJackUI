using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace DataJackUIGui.Services;

public static class SteamDelistedHelper
{
    private static readonly string DetailsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DataJackUIGui",
        "details");

    private static readonly ConcurrentDictionary<long, bool> _memoryCache = new();
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(6) };

    /// <summary>
    /// Synchronously checks if a game is known to be delisted from RAM or cached details on disk.
    /// Returns true if delisted, false if active, null if not cached.
    /// </summary>
    public static bool? CheckDelistedFast(long appid)
    {
        if (_memoryCache.TryGetValue(appid, out bool delisted))
            return delisted;

        try
        {
            var path = Path.Combine(DetailsDir, $"{appid}.json");
            if (!File.Exists(path)) return null;

            var raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw) || raw == "{}")
            {
                _memoryCache[appid] = true;
                return true;
            }

            using var doc = JsonDocument.Parse(raw);
            var d = doc.RootElement;

            bool isFree = d.TryGetProperty("is_free", out var f) && f.GetBoolean();
            bool hasPrice = d.TryGetProperty("price_overview", out _);
            int pkgCount = d.TryGetProperty("package_groups", out var pkgs) && pkgs.ValueKind == JsonValueKind.Array
                ? pkgs.GetArrayLength()
                : 0;

            bool isDelisted = (!isFree && !hasPrice && pkgCount == 0);
            _memoryCache[appid] = isDelisted;
            return isDelisted;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Explicitly mark an app as delisted or not in memory (e.g. from store search vs local list).
    /// </summary>
    public static void MarkDelisted(long appid, bool isDelisted)
    {
        _memoryCache[appid] = isDelisted;
    }

    /// <summary>
    /// Asynchronously verifies whether an app is delisted via disk or Steam API.
    /// </summary>
    public static async Task<bool> IsDelistedAsync(long appid, CancellationToken ct = default)
    {
        var fast = CheckDelistedFast(appid);
        if (fast.HasValue) return fast.Value;

        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appid}&filters=basic,price_overview&cc=US&l=english";
            using var res = await _httpClient.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                // Can't determine conclusively from network error, leave false
                return false;
            }

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(appid.ToString(), out var entry))
                return false;

            if (!entry.TryGetProperty("success", out var s) || !s.GetBoolean())
            {
                // Removed from Steam entirely
                _memoryCache[appid] = true;
                return true;
            }

            if (entry.TryGetProperty("data", out var data))
            {
                bool isFree = data.TryGetProperty("is_free", out var f) && f.GetBoolean();
                bool hasPrice = data.TryGetProperty("price_overview", out _);
                bool isDelisted = (!isFree && !hasPrice);
                _memoryCache[appid] = isDelisted;
                return isDelisted;
            }
        }
        catch
        {
            // Network or parse error
        }

        return false;
    }
}
