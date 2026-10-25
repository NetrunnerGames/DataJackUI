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

    private static readonly string DelistedCacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DataJackUIGui",
        "delisted_apps.json");

    private static readonly ConcurrentDictionary<long, bool> _memoryCache = new();
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static bool _delistedListLoaded = false;
    private static readonly object _syncLock = new();

    /// <summary>
    /// Loads known delisted AppIDs from local cache and syncs with steam-tracker in background.
    /// </summary>
    public static async Task EnsureDelistedListLoadedAsync()
    {
        if (_delistedListLoaded) return;

        lock (_syncLock)
        {
            if (_delistedListLoaded) return;
            _delistedListLoaded = true;
        }

        // 1. Load cached delisted AppIDs from disk if present
        try
        {
            if (File.Exists(DelistedCacheFile))
            {
                var json = await File.ReadAllTextAsync(DelistedCacheFile);
                var ids = JsonSerializer.Deserialize<HashSet<long>>(json);
                if (ids is not null)
                {
                    foreach (var id in ids)
                    {
                        _memoryCache[id] = true;
                    }
                }
            }
        }
        catch { }

        // 2. Fetch fresh list from steam-tracker in the background (cached for 7 days)
        _ = Task.Run(async () =>
        {
            try
            {
                var fileInfo = new FileInfo(DelistedCacheFile);
                if (fileInfo.Exists && (DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalDays < 7)
                {
                    return; // Cache is fresh enough
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, "https://steam-tracker.com/api?action=GetAppListV3");
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                using var resp = await _httpClient.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return;

                using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                if (doc.RootElement.TryGetProperty("removed_apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
                {
                    var idSet = new HashSet<long>();
                    foreach (var item in apps.EnumerateArray())
                    {
                        if (item.TryGetProperty("appid", out var appidProp) && appidProp.TryGetInt64(out var aid))
                        {
                            idSet.Add(aid);
                            _memoryCache[aid] = true;
                        }
                    }

                    if (idSet.Count > 0)
                    {
                        var dir = Path.GetDirectoryName(DelistedCacheFile);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        await File.WriteAllTextAsync(DelistedCacheFile, JsonSerializer.Serialize(idSet));
                    }
                }
            }
            catch { }
        });
    }

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
