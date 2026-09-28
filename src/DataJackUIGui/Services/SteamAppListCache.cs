using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace DataJackUIGui.Services;

/// <summary>
/// Bulk appid → name lookup. Primary sources are Ryuu and DepotBox gamelists in priority order,
/// falling back to Morrenus / Steam GetAppList if unfulfilled.
/// Cached to disk and refreshed periodically. Used for game names; covers come from Steam CDN.
/// </summary>
public class SteamAppListCache
{
    private static readonly string[] GamelistUrls =
    [
        "https://generator.ryuu.lol/gamelist",
        "https://generator.ryuu.lol/api/gamelist",
        "https://generator.ryuu.lol/api/games",
        "http://167.235.229.108/gamelist",
        "http://167.235.229.108/api/gamelist",
        "https://depotbox.org/gamelist",
        "https://depotbox.org/api/gamelist",
        "http://depotbox.org/gamelist",
        "https://api.steampowered.com/ISteamApps/GetAppList/v2/"
    ];

    private static readonly string CacheFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUI", "steam-applist.json");
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentDictionary<long, string> _names = new();
    private Task? _loadTask;

    public string? GetName(long appid) => _names.TryGetValue(appid, out var n) ? n : null;

    /// <summary>Ensure the name list is loaded (from disk, or downloaded once). Safe to call repeatedly.</summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    private async Task LoadAsync()
    {
        // Fresh disk cache → use it.
        try
        {
            if (File.Exists(CacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile) < MaxAge)
            {
                var map = JsonSerializer.Deserialize<Dictionary<long, string>>(await File.ReadAllTextAsync(CacheFile));
                if (map is { Count: > 0 })
                {
                    foreach (var (k, v) in map) _names[k] = v;
                    return;
                }
            }
        }
        catch { /* fall through to download */ }

        // Try sources in priority order: Ryuu -> DepotBox -> Morrenus -> Steam
        foreach (var url in GamelistUrls)
        {
            if (await TryDownloadAsync(url))
            {
                await SaveAsync();
                return;
            }
        }

        // All download sources failed: fall back to a stale disk cache if available.
        try
        {
            if (_names.IsEmpty && File.Exists(CacheFile))
            {
                var map = JsonSerializer.Deserialize<Dictionary<long, string>>(await File.ReadAllTextAsync(CacheFile));
                if (map is not null) foreach (var (k, v) in map) _names[k] = v;
            }
        }
        catch { /* give up. Names fall back to lua-parsed / appid */ }
    }

    /// <summary>
    /// Downloads and parses gamelist JSON from a given URL.
    /// Handles array of objects, map of appid->name, and wrapped applist structures.
    /// </summary>
    private async Task<bool> TryDownloadAsync(string url)
    {
        try
        {
            await using var stream = await _http.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            JsonElement apps = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("applist", out var applist) && applist.TryGetProperty("apps", out var applistApps))
                {
                    apps = applistApps;
                }
                else if (root.TryGetProperty("gamelist", out var gamelist))
                {
                    apps = gamelist;
                }
                else if (root.TryGetProperty("games", out var games))
                {
                    apps = games;
                }
                else
                {
                    // Could be a map of appid string -> name/object
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (long.TryParse(prop.Name, out long id))
                        {
                            if (prop.Value.ValueKind == JsonValueKind.String)
                            {
                                var val = prop.Value.GetString();
                                if (!string.IsNullOrWhiteSpace(val)) _names[id] = val;
                            }
                            else if (prop.Value.ValueKind == JsonValueKind.Object)
                            {
                                string? n = GetNameProperty(prop.Value);
                                if (!string.IsNullOrWhiteSpace(n)) _names[id] = n;
                            }
                        }
                    }
                    return !_names.IsEmpty;
                }
            }

            if (apps.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in apps.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        long id = 0;
                        if (item.TryGetProperty("appid", out var p1)) id = GetLong(p1);
                        else if (item.TryGetProperty("appId", out var p2)) id = GetLong(p2);
                        else if (item.TryGetProperty("app_id", out var p3)) id = GetLong(p3);
                        else if (item.TryGetProperty("id", out var p4)) id = GetLong(p4);

                        string? name = GetNameProperty(item);

                        if (id > 0 && !string.IsNullOrWhiteSpace(name))
                        {
                            _names[id] = name;
                        }
                    }
                }
            }

            return !_names.IsEmpty;
        }
        catch
        {
            return false;
        }
    }

    private static long GetLong(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.GetInt64();
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out long id)) return id;
        return 0;
    }

    private static string? GetNameProperty(JsonElement el)
    {
        if (el.TryGetProperty("name", out var n1)) return n1.GetString();
        if (el.TryGetProperty("title", out var n2)) return n2.GetString();
        if (el.TryGetProperty("game_name", out var n3)) return n3.GetString();
        return null;
    }

    private async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            await File.WriteAllTextAsync(CacheFile, JsonSerializer.Serialize(_names));
        }
        catch { /* best effort */ }
    }
}
