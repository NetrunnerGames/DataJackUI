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

    public void AddOrUpdate(long appId, string name)
    {
        if (appId > 0 && !string.IsNullOrWhiteSpace(name)) _names[appId] = name;
    }

    /// <summary>Ensure the name list is loaded (from disk, or downloaded once). Safe to call repeatedly.</summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    /// <summary>
    /// Searches cached app names for matches to query terms or AppID.
    /// Returns matches sorted by exact match -> starts with -> contains, then title length.
    /// </summary>
    public List<DataJackUIGui.Models.SteamSearchResult> Search(string query, int maxResults = 12)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        query = query.Trim();

        bool isNumeric = long.TryParse(query, out long targetAppId);
        var matches = new List<(long AppId, string Name, int Rank, int Length)>();

        foreach (var (appId, name) in _names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            int rank = -1;

            if (isNumeric && appId == targetAppId)
            {
                rank = 0; // Direct AppID match
            }
            else if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                rank = 0; // Exact title match
            }
            else if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                rank = 1; // Starts with query
            }
            else if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                rank = 2; // Contains query
            }

            if (rank >= 0)
            {
                matches.Add((appId, name, rank, name.Length));
            }
        }

        return matches
            .OrderBy(m => m.Rank)
            .ThenBy(m => m.Length)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(m => new DataJackUIGui.Models.SteamSearchResult { AppId = m.AppId, Name = m.Name })
            .ToList();
    }

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

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("applist", out var al) && al.TryGetProperty("apps", out var apps)) return ParseArray(apps);
                if (root.TryGetProperty("gamelist", out var gl)) return ParseArray(gl);
                if (root.TryGetProperty("games", out var gs)) return ParseArray(gs);
                return ParseObjectMap(root);
            }

            if (root.ValueKind == JsonValueKind.Array) return ParseArray(root);
            return !_names.IsEmpty;
        }
        catch { return false; }
    }

    private bool ParseObjectMap(JsonElement root)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (!long.TryParse(prop.Name, out long id)) continue;
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                if (prop.Value.GetString() is { } val && !string.IsNullOrWhiteSpace(val)) _names[id] = val;
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                if (GetNameProperty(prop.Value) is { } n && !string.IsNullOrWhiteSpace(n)) _names[id] = n;
            }
        }
        return !_names.IsEmpty;
    }

    private bool ParseArray(JsonElement apps)
    {
        if (apps.ValueKind != JsonValueKind.Array) return !_names.IsEmpty;
        foreach (var item in apps.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            long id = 0;
            if (item.TryGetProperty("appid", out var p1)) id = GetLong(p1);
            else if (item.TryGetProperty("appId", out var p2)) id = GetLong(p2);
            else if (item.TryGetProperty("app_id", out var p3)) id = GetLong(p3);
            else if (item.TryGetProperty("id", out var p4)) id = GetLong(p4);

            if (id > 0 && GetNameProperty(item) is { } name && !string.IsNullOrWhiteSpace(name)) _names[id] = name;
        }
        return !_names.IsEmpty;
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
