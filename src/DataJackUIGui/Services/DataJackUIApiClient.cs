using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataJackUIGui.Models;
using DataJackUIGui.Services.Downloads;

namespace DataJackUIGui.Services;

public class ApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public record DownloadedFile(string FilePath, string FileName);

/// <summary>Typed client for the DataJackUI backend API, authenticated with a Supabase bearer token.</summary>
public class DataJackUIApiClient
{
    static DataJackUIApiClient() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly AuthService _auth;
    private readonly SteamAppInfoCache _appInfo;
    private readonly SteamAppListCache _appList;
    private readonly CoverCache _covers;
    private readonly SettingsService _settings;

    public DataJackUIApiClient(AuthService auth, SteamAppInfoCache appInfo, SteamAppListCache appList, CoverCache covers, SettingsService settings)
    {
        _auth = auth;
        _appInfo = appInfo;
        _appList = appList;
        _covers = covers;
        _settings = settings;

        var doh = new DohResolver();
        var handler = new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
            },
            ConnectCallback = async (context, cancellationToken) =>
            {
                string mode = _settings.DnsMode; // "Auto" | "Always" | "Never"
                string host = context.DnsEndPoint.Host;
                int port = context.DnsEndPoint.Port;

                if (mode == "Always" && !DohResolver.ShouldBypass(host))
                {
                    var addrs = await doh.ResolveAsync(host, cancellationToken);
                    if (addrs.Length > 0)
                    {
                        var s = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                        await s.ConnectAsync(addrs[0], port, cancellationToken);
                        return new System.Net.Sockets.NetworkStream(s, ownsSocket: true);
                    }
                }

                try
                {
                    var s = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                    await s.ConnectAsync(host, port, cancellationToken);
                    return new System.Net.Sockets.NetworkStream(s, ownsSocket: true);
                }
                catch when (mode != "Never" && !DohResolver.ShouldBypass(host))
                {
                    var addrs = await doh.ResolveAsync(host, cancellationToken);
                    if (addrs.Length > 0)
                    {
                        var s = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                        await s.ConnectAsync(addrs[0], port, cancellationToken);
                        return new System.Net.Sockets.NetworkStream(s, ownsSocket: true);
                    }
                    throw;
                }
            }
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(AppConfig.ApiBaseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    // ── Endpoints ───────────────────────────────────────────────────

    public async Task<List<SteamSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var localTask = Task.Run(async () =>
        {
            try { await _appList.EnsureLoadedAsync(); } catch { }
            return _appList.Search(query, maxResults: 12);
        }, ct);

        var storeTask = Task.Run(async () =>
        {
            var url = $"{AppConfig.SteamStoreSearchUrl}?term={Uri.EscapeDataString(query)}&l=english&cc=US";
            try
            {
                var res = await _http.GetAsync(url, ct);
                if (res.IsSuccessStatusCode)
                {
                    var data = await ReadJsonAsync<SteamStoreSearchResponse>(res, ct);
                    return (data?.Items ?? []).Take(8).Select(i => new SteamSearchResult { AppId = i.Id, Name = i.Name }).ToList();
                }
            }
            catch { }
            return new List<SteamSearchResult>();
        }, ct);

        await Task.WhenAll(localTask, storeTask);

        var localResults = localTask.Result;
        var storeResults = storeTask.Result;

        var storeAppIds = storeResults.Select(s => s.AppId).ToHashSet();
        foreach (var s in storeResults)
        {
            s.IsDelisted = false;
            SteamDelistedHelper.MarkDelisted(s.AppId, false);
        }

        foreach (var l in localResults)
        {
            var cachedStatus = SteamDelistedHelper.CheckDelistedFast(l.AppId);
            if (cachedStatus.HasValue)
            {
                l.IsDelisted = cachedStatus.Value;
            }
            else if (storeResults.Count > 0 && !storeAppIds.Contains(l.AppId))
            {
                // Delisted or store search omitted it; will be refined by card check
                l.IsDelisted = false;
            }
        }

        var combined = localResults
            .Concat(storeResults)
            .DistinctBy(r => r.AppId)
            .Take(12)
            .ToList();

        return combined;
    }

    /// <summary>Steam's top sellers (from global charts) and new releases lists for the Add page strips.
    /// Public, no auth required. Uses Cloudflare CDN for images.</summary>
    /// <summary>
    /// Fetches top most played games from ISteamChartsService/GetMostPlayedGames/v1
    /// and resolves library_hero imagery + game names for the Add page carousel.
    /// </summary>
    private static readonly string PopularCacheFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUIGui", "popular_games_cache.json");
    private static readonly TimeSpan PopularCacheMaxAge = TimeSpan.FromHours(4);

    public async Task<List<PopularHeroGame>> GetPopularGamesAsync(CancellationToken ct = default)
    {
        // 1. Try reading disk cache first if fresh
        try
        {
            if (File.Exists(PopularCacheFile))
            {
                var fi = new FileInfo(PopularCacheFile);
                if (DateTime.UtcNow - fi.LastWriteTimeUtc < PopularCacheMaxAge)
                {
                    string json = await File.ReadAllTextAsync(PopularCacheFile, ct);
                    var cachedList = JsonSerializer.Deserialize<List<PopularHeroGame>>(json);
                    if (cachedList is { Count: > 0 } &&
                        !cachedList.Any(g => g.AppId == 578080) &&
                        !cachedList.Any(g => (g.AppId is 1867240 or 3393110) && g.HeroImage.EndsWith("/library_hero.jpg") && !g.HeroImage.Contains("/" + g.AppId + "/")))
                    {
                        // Check if local hero files exist for instant loading
                        foreach (var g in cachedList)
                        {
                            g.LocalHero = _covers.GetLocalHeroPath(g.AppId);
                        }
                        return cachedList;
                    }
                }
            }
        }
        catch { /* continue to fresh fetch */ }

        try
        {
            var url = "https://api.steampowered.com/ISteamChartsService/GetMostPlayedGames/v1/";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return await LoadStalePopularCacheAsync(ct);
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("response", out var r) ||
                !r.TryGetProperty("ranks", out var ranks) ||
                ranks.ValueKind != JsonValueKind.Array)
            {
                return await LoadStalePopularCacheAsync(ct);
            }

            var candidates = new List<(long AppId, int Rank, int Peak)>();
            foreach (var rankItem in ranks.EnumerateArray())
            {
                if (!rankItem.TryGetProperty("appid", out var appidProp) || !appidProp.TryGetInt64(out var aid) || aid <= 0)
                    continue;

                // Exclude PUBG: BATTLEGROUNDS (578080) as a free game that doesn't need unlockers
                if (aid == 578080) continue;

                int rank = rankItem.TryGetProperty("rank", out var rankProp) && rankProp.TryGetInt32(out var rVal) ? rVal : candidates.Count + 1;
                int peak = rankItem.TryGetProperty("peak_in_game", out var peakProp) && peakProp.TryGetInt32(out var pVal) ? pVal : 0;
                candidates.Add((aid, rank, peak));
                if (candidates.Count >= 10) break;
            }

            // Batch resolve exact library hero artwork (with content hashes) and titles via Valve's IStoreBrowseService
            var storeAssets = await FetchStoreBrowseHeroAssetsAsync(candidates.Select(c => c.AppId), ct);

            var list = new List<PopularHeroGame>();
            foreach (var (aid, rank, peak) in candidates)
            {
                storeAssets.TryGetValue(aid, out var meta);

                string? name = meta.Name;
                if (string.IsNullOrWhiteSpace(name)) name = _appList.GetName(aid);
                if (string.IsNullOrWhiteSpace(name)) name = _appInfo.GetCached(aid)?.Name;
                if (string.IsNullOrWhiteSpace(name)) name = $"App {aid}";

                string heroImg = meta.HeroUrl ?? $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{aid}/library_hero.jpg";

                var game = new PopularHeroGame
                {
                    AppId = aid,
                    Name = name,
                    Rank = rank,
                    PeakInGame = peak,
                    HeroImage = heroImg,
                    LocalHero = _covers.GetLocalHeroPath(aid)
                };

                list.Add(game);
                if (list.Count >= 10) break;
            }

            // Save to disk cache
            try
            {
                string dir = Path.GetDirectoryName(PopularCacheFile)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(list);
                await File.WriteAllTextAsync(PopularCacheFile, json, ct);
            }
            catch { /* best effort cache write */ }

            return list;
        }
        catch
        {
            return await LoadStalePopularCacheAsync(ct);
        }
    }

    private sealed record StoreBrowseItemMeta(
        string? Name,
        string? HeroUrl,
        bool IsFree,
        bool Visible,
        int Success,
        int Type);

    private async Task<Dictionary<long, StoreBrowseItemMeta>> FetchStoreBrowseMetadataAsync(
        IEnumerable<long> appids, CancellationToken ct)
    {
        var result = new Dictionary<long, StoreBrowseItemMeta>();
        try
        {
            var idList = appids.Select(id => new { appid = id }).ToList();
            if (idList.Count == 0) return result;

            var payload = new
            {
                ids = idList,
                context = new { language = "english", country_code = "US" },
                data_request = new { include_assets = true, include_basic_info = true }
            };

            string jsonStr = JsonSerializer.Serialize(payload);
            string url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(jsonStr);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return result;

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("response", out var r) &&
                r.TryGetProperty("store_items", out var storeItems) &&
                storeItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in storeItems.EnumerateArray())
                {
                    if (!item.TryGetProperty("appid", out var aidProp) || !aidProp.TryGetInt64(out var aid))
                        continue;

                    string? name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    bool isFree = item.TryGetProperty("is_free", out var ifProp) && ifProp.ValueKind == JsonValueKind.True;
                    bool visible = !item.TryGetProperty("visible", out var vProp) || vProp.ValueKind != JsonValueKind.False;
                    int success = item.TryGetProperty("success", out var sProp) && sProp.TryGetInt32(out var sVal) ? sVal : 1;
                    int type = item.TryGetProperty("type", out var tProp) && tProp.TryGetInt32(out var tVal) ? tVal : 0;

                    string? heroUrl = null;
                    if (item.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Object)
                    {
                        string? heroRel = null;
                        if (assets.TryGetProperty("library_hero", out var heroProp) && heroProp.GetString() is { Length: > 0 } h)
                            heroRel = h;
                        else if (assets.TryGetProperty("library_hero_2x", out var h2x) && h2x.GetString() is { Length: > 0 } h2)
                            heroRel = h2;
                        else if (assets.TryGetProperty("hero_capsule", out var hc) && hc.GetString() is { Length: > 0 } hcStr)
                            heroRel = hcStr;
                        else if (assets.TryGetProperty("main_capsule_2x", out var mc2) && mc2.GetString() is { Length: > 0 } mc2Str)
                            heroRel = mc2Str;
                        else if (assets.TryGetProperty("main_capsule", out var mc) && mc.GetString() is { Length: > 0 } mcStr)
                            heroRel = mcStr;

                        if (heroRel is not null)
                        {
                            string fmt = assets.TryGetProperty("asset_url_format", out var fmtProp) && fmtProp.GetString() is { Length: > 0 } fStr
                                ? fStr
                                : $"steam/apps/{aid}/${{FILENAME}}";

                            string rel = fmt.Replace("${FILENAME}", heroRel).Split('?')[0];
                            heroUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/{rel}";
                        }
                    }

                    result[aid] = new StoreBrowseItemMeta(name, heroUrl, isFree, visible, success, type);
                }
            }
        }
        catch { /* best effort */ }

        return result;
    }

    private async Task<Dictionary<long, (string? Name, string? HeroUrl)>> FetchStoreBrowseHeroAssetsAsync(
        IEnumerable<long> appids, CancellationToken ct)
    {
        var metaDict = await FetchStoreBrowseMetadataAsync(appids, ct);
        return metaDict.ToDictionary(kvp => kvp.Key, kvp => (kvp.Value.Name, kvp.Value.HeroUrl));
    }

    private static async Task<List<PopularHeroGame>> LoadStalePopularCacheAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(PopularCacheFile))
            {
                string json = await File.ReadAllTextAsync(PopularCacheFile, ct);
                var cached = JsonSerializer.Deserialize<List<PopularHeroGame>>(json);
                if (cached is { Count: > 0 })
                {
                    cached.RemoveAll(g => g.AppId == 578080);
                    return cached;
                }
            }
        }
        catch { }
        return [];
    }

    private static readonly string WeeklyTopSellersCacheFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUIGui", "weekly_top_sellers_paid_global_cache.json");
    private static readonly string LatestMonthHitsCacheFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUIGui", "latest_month_hits_paid_cache.json");
    private static readonly TimeSpan BrowseCacheMaxAge = TimeSpan.FromHours(4);

    public async Task<List<StoreBrowseItem>> GetWeeklyTopSellersAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(WeeklyTopSellersCacheFile))
            {
                var fi = new FileInfo(WeeklyTopSellersCacheFile);
                if (DateTime.UtcNow - fi.LastWriteTimeUtc < BrowseCacheMaxAge)
                {
                    string json = await File.ReadAllTextAsync(WeeklyTopSellersCacheFile, ct);
                    var cachedList = JsonSerializer.Deserialize<List<StoreBrowseItem>>(json);
                    if (cachedList is { Count: > 0 })
                    {
                        return cachedList;
                    }
                }
            }
        }
        catch { }

        try
        {
            var payload = new
            {
                context = new { country_code = "WW" },
                data_request = new { include_assets = true, include_basic_info = true },
                page_start = 0,
                page_count = 35
            };
            string jsonPayload = JsonSerializer.Serialize(payload);
            string url = "https://api.steampowered.com/IStoreTopSellersService/GetWeeklyTopSellers/v1/?input_json=" + Uri.EscapeDataString(jsonPayload);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return await LoadStaleBrowseCacheAsync(WeeklyTopSellersCacheFile, ct);
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("response", out var r) ||
                !r.TryGetProperty("ranks", out var ranks) ||
                ranks.ValueKind != JsonValueKind.Array)
            {
                return await LoadStaleBrowseCacheAsync(WeeklyTopSellersCacheFile, ct);
            }

            var list = new List<StoreBrowseItem>();
            foreach (var rankItem in ranks.EnumerateArray())
            {
                if (!rankItem.TryGetProperty("appid", out var appidProp) || !appidProp.TryGetInt64(out var aid) || aid <= 0)
                    continue;

                // Skip hardware (e.g. Steam Deck 1675200)
                if (aid == 1675200) continue;

                bool isFree = false;
                bool isVisible = true;
                int itemType = 0;
                string? name = null;

                if (rankItem.TryGetProperty("item", out var itemProp))
                {
                    if (itemProp.TryGetProperty("is_free", out var fProp) && fProp.ValueKind == JsonValueKind.True)
                        isFree = true;
                    if (itemProp.TryGetProperty("visible", out var vProp) && vProp.ValueKind == JsonValueKind.False)
                        isVisible = false;
                    if (itemProp.TryGetProperty("type", out var tProp) && tProp.TryGetInt32(out var tVal))
                        itemType = tVal;
                    if (itemProp.TryGetProperty("name", out var nameProp))
                        name = nameProp.GetString();
                }

                // Strictly filter out free-to-play games
                if (isFree) continue;

                // Skip delisted or hidden items, and non-game items (hardware, etc.)
                if (!isVisible || itemType != 0) continue;
                if (SteamDelistedHelper.CheckDelistedFast(aid) == true) continue;

                if (string.IsNullOrWhiteSpace(name)) name = _appList.GetName(aid);
                if (string.IsNullOrWhiteSpace(name)) name = _appInfo.GetCached(aid)?.Name;
                if (string.IsNullOrWhiteSpace(name)) name = $"App {aid}";

                string coverUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{aid}/header.jpg";

                list.Add(new StoreBrowseItem
                {
                    AppId = aid,
                    Name = name,
                    CoverUrl = coverUrl
                });

                if (list.Count >= 10) break;
            }

            try
            {
                string dir = Path.GetDirectoryName(WeeklyTopSellersCacheFile)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(list);
                await File.WriteAllTextAsync(WeeklyTopSellersCacheFile, json, ct);
            }
            catch { }

            return list;
        }
        catch
        {
            return await LoadStaleBrowseCacheAsync(WeeklyTopSellersCacheFile, ct);
        }
    }

    public async Task<List<StoreBrowseItem>> GetLatestMonthHitsAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(LatestMonthHitsCacheFile))
            {
                var fi = new FileInfo(LatestMonthHitsCacheFile);
                if (DateTime.UtcNow - fi.LastWriteTimeUtc < BrowseCacheMaxAge)
                {
                    string json = await File.ReadAllTextAsync(LatestMonthHitsCacheFile, ct);
                    var cachedList = JsonSerializer.Deserialize<List<StoreBrowseItem>>(json);
                    if (cachedList is { Count: > 0 })
                    {
                        return cachedList;
                    }
                }
            }
        }
        catch { }

        try
        {
            var now = DateTime.UtcNow;
            var currentMonthDate = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            long rtime = new DateTimeOffset(currentMonthDate).ToUnixTimeSeconds();

            var appIds = await FetchMonthTopAppReleasesAppIdsAsync(rtime, ct);
            if (appIds.Count == 0)
            {
                var prevDate = currentMonthDate.AddMonths(-1);
                if (prevDate.Year == now.Year)
                {
                    long prevRtime = new DateTimeOffset(prevDate).ToUnixTimeSeconds();
                    appIds = await FetchMonthTopAppReleasesAppIdsAsync(prevRtime, ct);
                }
            }

            if (appIds.Count == 0)
            {
                return await LoadStaleBrowseCacheAsync(LatestMonthHitsCacheFile, ct);
            }

            // Fetch metadata for candidates (up to 35) to filter out free and delisted games
            var candidateIds = appIds.Take(35).ToList();
            var storeMeta = await FetchStoreBrowseMetadataAsync(candidateIds, ct);

            var list = new List<StoreBrowseItem>();
            foreach (var aid in candidateIds)
            {
                if (aid == 1675200) continue;
                if (SteamDelistedHelper.CheckDelistedFast(aid) == true) continue;

                if (storeMeta.TryGetValue(aid, out var meta))
                {
                    // Filter out free-to-play games
                    if (meta.IsFree) continue;

                    // Filter out delisted or hidden or non-game items
                    if (!meta.Visible || meta.Success != 1 || meta.Type != 0) continue;
                }

                string? name = meta?.Name;
                if (string.IsNullOrWhiteSpace(name)) name = _appList.GetName(aid);
                if (string.IsNullOrWhiteSpace(name)) name = _appInfo.GetCached(aid)?.Name;
                if (string.IsNullOrWhiteSpace(name)) name = $"App {aid}";

                string coverUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{aid}/header.jpg";

                list.Add(new StoreBrowseItem
                {
                    AppId = aid,
                    Name = name,
                    CoverUrl = coverUrl
                });

                if (list.Count >= 10) break;
            }

            try
            {
                string dir = Path.GetDirectoryName(LatestMonthHitsCacheFile)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(list);
                await File.WriteAllTextAsync(LatestMonthHitsCacheFile, json, ct);
            }
            catch { }

            return list;
        }
        catch
        {
            return await LoadStaleBrowseCacheAsync(LatestMonthHitsCacheFile, ct);
        }
    }

    private async Task<List<long>> FetchMonthTopAppReleasesAppIdsAsync(long rtimeMonth, CancellationToken ct)
    {
        var result = new List<long>();
        try
        {
            string url = $"https://api.steampowered.com/ISteamChartsService/GetMonthTopAppReleases/v1/?rtime_month={rtimeMonth}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return result;

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("response", out var r) &&
                r.TryGetProperty("top_combined_app_and_dlc_releases", out var releases) &&
                releases.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in releases.EnumerateArray())
                {
                    if (item.TryGetProperty("appid", out var aidProp) && aidProp.TryGetInt64(out var aid) && aid > 0)
                    {
                        result.Add(aid);
                    }
                }
            }
        }
        catch { }
        return result;
    }

    private static async Task<List<StoreBrowseItem>> LoadStaleBrowseCacheAsync(string cacheFile, CancellationToken ct)
    {
        try
        {
            if (File.Exists(cacheFile))
            {
                string json = await File.ReadAllTextAsync(cacheFile, ct);
                var cached = JsonSerializer.Deserialize<List<StoreBrowseItem>>(json);
                if (cached is { Count: > 0 })
                {
                    return cached;
                }
            }
        }
        catch { }
        return [];
    }

    public async Task<(List<SteamFeaturedItem> TopSellers, List<SteamFeaturedItem> NewReleases)> GetFeaturedAsync(
        CancellationToken ct = default)
    {
        var topSellersTask = FetchTopSellersGlobalAsync(ct);
        var newReleasesTask = FetchNewReleasesAsync(ct);
        await Task.WhenAll(topSellersTask, newReleasesTask);

        return (topSellersTask.Result, newReleasesTask.Result);
    }

    private async Task<List<SteamFeaturedItem>> FetchTopSellersGlobalAsync(CancellationToken ct)
    {
        try
        {
            var url = "https://store.steampowered.com/charts/topselling/global";
            var res = await _http.GetAsync(url, ct);
            if (res.IsSuccessStatusCode)
            {
                var html = await res.Content.ReadAsStringAsync(ct);
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    html, @"href=""https://store\.steampowered\.com/app/(\d+)/");

                var appIds = new List<long>();
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    if (long.TryParse(m.Groups[1].Value, out long aid) && aid > 0 && !appIds.Contains(aid))
                    {
                        appIds.Add(aid);
                        if (appIds.Count >= 20) break;
                    }
                }

                if (appIds.Count > 0)
                {
                    var items = new List<SteamFeaturedItem>();
                    foreach (var aid in appIds)
                    {
                        var info = _appInfo.GetCached(aid) ?? await _appInfo.ResolveAsync(aid, ct);
                        string name = _appList.GetName(aid) ?? info?.Name ?? aid.ToString();
                        string imgUrl = await ResolveAndCacheCoverUrlAsync(aid, null, ct);

                        items.Add(new SteamFeaturedItem
                        {
                            Id = aid,
                            Name = name,
                            Type = 0,
                            LargeCapsuleImage = imgUrl
                        });
                    }
                    return items;
                }
            }
        }
        catch { }

        return await FetchSearchCategoryAsync("filter=global_topsellers&category1=998", ct);
    }

    private async Task<List<SteamFeaturedItem>> FetchNewReleasesAsync(CancellationToken ct)
    {
        try
        {
            var res = await _http.GetAsync("https://store.steampowered.com/explore/new/", ct);
            if (res.IsSuccessStatusCode)
            {
                var html = await res.Content.ReadAsStringAsync(ct);
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    html, @"(?i)(?:href=""https://store\.steampowered\.com/app/|data-ds-appid="")(\d+)");

                var appIds = new List<long>();
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    if (long.TryParse(m.Groups[1].Value, out long aid) && aid > 0 && !appIds.Contains(aid))
                    {
                        appIds.Add(aid);
                        if (appIds.Count >= 20) break;
                    }
                }

                if (appIds.Count > 0)
                {
                    var items = new List<SteamFeaturedItem>();
                    foreach (var aid in appIds)
                    {
                        var info = _appInfo.GetCached(aid) ?? await _appInfo.ResolveAsync(aid, ct);
                        string name = _appList.GetName(aid) ?? info?.Name ?? aid.ToString();
                        string imgUrl = await ResolveAndCacheCoverUrlAsync(aid, null, ct);

                        items.Add(new SteamFeaturedItem
                        {
                            Id = aid,
                            Name = name,
                            Type = 0,
                            LargeCapsuleImage = imgUrl
                        });
                    }
                    return items;
                }
            }
        }
        catch { }

        try
        {
            var res = await _http.GetAsync($"{AppConfig.SteamFeaturedUrl}?cc=us&l=english", ct);
            if (res.IsSuccessStatusCode)
            {
                var data = await ReadJsonAsync<SteamFeaturedResponse>(res, ct);
                var rawNew = data?.NewReleases?.Items;
                if (rawNew is { Count: > 0 })
                {
                    var items = new List<SteamFeaturedItem>();
                    foreach (var i in rawNew)
                    {
                        if (i.Type == 0 && i.Id > 0)
                        {
                            string imgUrl = await ResolveAndCacheCoverUrlAsync(i.Id, i.LargeCapsuleImage, ct);

                            items.Add(new SteamFeaturedItem
                            {
                                Id = i.Id,
                                Name = i.Name,
                                Type = i.Type,
                                LargeCapsuleImage = imgUrl
                            });
                        }
                    }
                    var distinct = items.DistinctBy(x => x.Id).Take(20).ToList();
                    if (distinct.Count > 0) return distinct;
                }
            }
        }
        catch { }

        return await FetchSearchCategoryAsync("filter=popularnew&sort_by=Released_DESC", ct);
    }

    private async Task<string> ResolveAndCacheCoverUrlAsync(long appId, string? fallbackUrl, CancellationToken ct)
    {
        try
        {
            var info = _appInfo.GetCached(appId) ?? await _appInfo.ResolveAsync(appId, ct);
            string rawUrl = info?.HeaderImage ?? fallbackUrl ?? SteamAppInfoCache.GuessHeaderImageUrl(appId);
            string sanitizedUrl = SteamCdnUrl.Sanitize(rawUrl) ?? rawUrl;
            string? localPath = await _covers.EnsureAsync(appId, sanitizedUrl, ct);
            return localPath ?? sanitizedUrl;
        }
        catch
        {
            string fallback = SteamAppInfoCache.GuessHeaderImageUrl(appId);
            return SteamCdnUrl.Sanitize(fallback) ?? fallback;
        }
    }

    private async Task<List<SteamFeaturedItem>> FetchSearchCategoryAsync(string queryParams, CancellationToken ct)
    {
        try
        {
            var url = $"https://store.steampowered.com/search/results/?query=&start=0&count=20&{queryParams}&infinite=1";
            var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return [];

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results_html", out var htmlProp)) return [];

            var html = htmlProp.GetString();
            if (string.IsNullOrEmpty(html)) return [];

            var matches = System.Text.RegularExpressions.Regex.Matches(
                html,
                @"(?s)<a [^>]*data-ds-appid=""(\d+)""[^>]*>.*?<span class=""title"">(.*?)</span>");

            var items = new List<SteamFeaturedItem>();
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (long.TryParse(m.Groups[1].Value, out long appId))
                {
                    string name = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                    string imgUrl = await ResolveAndCacheCoverUrlAsync(appId, null, ct);
                    items.Add(new SteamFeaturedItem
                    {
                        Id = appId,
                        Name = name,
                        Type = 0,
                        LargeCapsuleImage = imgUrl
                    });
                }
            }

            return items.DistinctBy(i => i.Id).Take(20).ToList();
        }
        catch { return []; }
    }

    /// <summary>Public endpoint, no auth required.</summary>
    /// <summary>Game metadata straight from Steam's appdetails (cached to details\&lt;appid&gt;.json via the
    /// throttle, interactive priority), no lua.tools proxy. ANY fetch path funnels through here (normal /
    /// DLC / fast / plugin add), so this is also where the header image gets warmed into covers\.</summary>
    public async Task<GameDetails?> GetDetailsAsync(string appid, CancellationToken ct = default)
    {
        if (!long.TryParse(appid, out long id)) return null;
        var details = await _appInfo.ResolveGameDetailsAsync(id, ct);
        if (details is { HeaderImage: { Length: > 0 } img })
            _ = _covers.EnsureAsync(id, img, CancellationToken.None); // warm the cover cache (best-effort)
        return details;
    }

    /// <summary>
    /// Fetches manifest info via auth worker proxy (or direct Ryuu backend).
    /// Returns Steam's latest depot manifests, files in Ryuu DB, and DLC list without downloading any files or spamming the server.
    /// </summary>
    public async Task<RyuuManifestInfo?> GetRyuuManifestInfoAsync(string appid, CancellationToken ct = default)
    {
        string[] endpoints =
        [
            $"{AppConfig.ApiBaseUrl}/api/ryuu/manifestinfo/{appid}",
            $"{AppConfig.ManifestBackendUrl}/manifestinfo/{appid}"
        ];

        foreach (var endpoint in endpoints)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                req.Headers.TryAddWithoutValidation("User-Agent", AppConfig.DefaultUserAgent);
                using var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    var parsed = await ReadJsonAsync<RyuuManifestInfo>(res, ct);
                    if (parsed != null) return parsed;
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Sends a HEAD request to check if Ryuu has manifest information for this appid.
    /// Per Ryuu's API guidelines, HEAD requests don't download any files and are safe for availability checking.
    /// </summary>
    public async Task<bool> CheckRyuuAvailabilityHeadAsync(string appid, CancellationToken ct = default)
    {
        string[] endpoints =
        [
            $"{AppConfig.ApiBaseUrl}/api/ryuu/manifestinfo/{appid}",
            $"{AppConfig.ManifestBackendUrl}/manifestinfo/{appid}"
        ];

        foreach (var endpoint in endpoints)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2.5));

                var req = new HttpRequestMessage(HttpMethod.Head, endpoint);
                req.Headers.TryAddWithoutValidation("User-Agent", AppConfig.DefaultUserAgent);
                using var res = await _http.SendAsync(req, timeoutCts.Token);
                if (res.StatusCode == HttpStatusCode.OK) return true;
            }
            catch { }
        }

        return false;
    }

    /// <summary>
    /// Requests Ryuu to update manifests for an AppID on the specified branch via auth worker.
    /// </summary>
    public async Task<bool> RequestRyuuUpdateAsync(string appid, string branch = "public", CancellationToken ct = default)
    {
        string[] endpoints =
        [
            $"{AppConfig.ApiBaseUrl}/api/ryuu/requestupdate?appid={Uri.EscapeDataString(appid)}&branch={Uri.EscapeDataString(branch)}",
            $"{AppConfig.ManifestBackendUrl}/requestupdate?appid={Uri.EscapeDataString(appid)}&branch={Uri.EscapeDataString(branch)}"
        ];

        foreach (var endpoint in endpoints)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                req.Headers.TryAddWithoutValidation("User-Agent", AppConfig.DefaultUserAgent);
                using var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) return true;
            }
            catch { }
        }

        return false;
    }

    /// <summary>Source name → "available" | "unavailable" | other status. Priority: 1. DepotBox, 2. Ryuu, 3. Public.</summary>
    public async Task<Dictionary<string, string>> CheckSourcesAsync(string appid, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Run Ryuu, DepotBox, and Sushi checks concurrently in parallel with a strict 3.5s timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3.5));

        var ryuuTask = CheckRyuuAvailabilityHeadAsync(appid, cts.Token);
        var depotBoxTask = CheckDepotBoxManifestAvailabilityAsync(appid, cts.Token);
        var sushiTask = CheckSushiAvailabilityHeadAsync(appid, cts.Token);

        await Task.WhenAll(
            ryuuTask.ContinueWith(_ => { }),
            depotBoxTask.ContinueWith(_ => { }),
            sushiTask.ContinueWith(_ => { })
        );

        bool ryuuAvailable = ryuuTask.IsCompletedSuccessfully && ryuuTask.Result;
        bool depotBoxAvailable = depotBoxTask.IsCompletedSuccessfully && depotBoxTask.Result;
        bool sushiAvailable = sushiTask.IsCompletedSuccessfully && sushiTask.Result;

        result["Ryuu"] = ryuuAvailable ? "available" : "unavailable";

        if (depotBoxAvailable || ryuuAvailable || (long.TryParse(appid, out long aid) && aid > 0 && _appList.GetName(aid) != null))
        {
            result["DepotBox"] = "available";
        }
        else
        {
            result["DepotBox"] = "unavailable";
        }

        result["Sushi"] = sushiAvailable ? "available" : "unavailable";

        return result;
    }

    /// <summary>
    /// Checks public manifest repository availability using a lightweight HEAD request (0 download bytes).
    /// </summary>
    public async Task<bool> CheckSushiAvailabilityHeadAsync(string appid, CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2.5));

            var req = new HttpRequestMessage(HttpMethod.Head, $"https://proxy.netrunnergames.workers.dev/gh/https://raw.githubusercontent.com/sushi-dev55-alt/sushitools-games-repo-alt/refs/heads/main/{appid}.zip");
            req.Headers.UserAgent.ParseAdd(AppConfig.DefaultUserAgent);
            using var res = await _http.SendAsync(req, timeoutCts.Token);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a game needs an update by comparing installed depot manifests against
    /// Steam's latest depot manifests from generator.ryuu.lol/manifestinfo/{appid}.
    /// Does NOT download any manifest files, completely avoiding rate-limit or bandwidth warnings.
    /// </summary>
    public async Task<GameUpdateCheckResult> CheckGameUpdateAsync(
        long appId,
        IReadOnlyDictionary<long, string> installedDepots,
        CancellationToken ct = default)
    {
        var info = await GetRyuuManifestInfoAsync(appId.ToString(), ct);
        if (info == null || !info.IsAvailable)
        {
            return new GameUpdateCheckResult(
                HasUpdate: false,
                RyuuHasNewManifests: false,
                OutdatedDepots: new Dictionary<long, string>(),
                LatestSteamDepots: new Dictionary<string, string>(),
                RyuuDatabaseFiles: Array.Empty<string>(),
                DlcInLua: Array.Empty<RyuuDlcInfo>());
        }

        var steamDepots = info.LatestSteamDepots;
        var ryuuFiles = info.DatabaseFiles;
        var outdated = new Dictionary<long, string>();
        bool ryuuHasAnyNew = false;

        if (installedDepots != null && installedDepots.Count > 0)
        {
            foreach (var (depotId, installedManifest) in installedDepots)
            {
                if (steamDepots.TryGetValue(depotId.ToString(), out string? latestSteamManifest))
                {
                    if (!string.Equals(installedManifest, latestSteamManifest, StringComparison.OrdinalIgnoreCase))
                    {
                        outdated[depotId] = latestSteamManifest;

                        // Check if Ryuu's database has the updated manifest file
                        string manifestFilename = $"{depotId}_{latestSteamManifest}.manifest";
                        if (ryuuFiles.Any(f => string.Equals(f, manifestFilename, StringComparison.OrdinalIgnoreCase)))
                        {
                            ryuuHasAnyNew = true;
                        }
                    }
                }
            }
        }

        var dlcInLua = info.Dlc?.Where(d => d.InLua).ToList() ?? [];

        return new GameUpdateCheckResult(
            HasUpdate: outdated.Count > 0,
            RyuuHasNewManifests: ryuuHasAnyNew,
            OutdatedDepots: outdated,
            LatestSteamDepots: steamDepots,
            RyuuDatabaseFiles: ryuuFiles,
            DlcInLua: dlcInLua);
    }

    /// <summary>
    /// The standard lua.tools daily download usage (25/day), counted live from the user_downloads
    /// table via Supabase REST. The same source the website reads. RLS scopes it to the signed-in
    /// user, so no user id is needed. Null on failure / not signed in.
    /// </summary>
    public async Task<StandardUsage?> GetStandardUsageAsync(CancellationToken ct = default)
    {
        try
        {
            var todayUtc = DateTime.UtcNow.Date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            string url = $"{AppConfig.SupabaseUrl}/rest/v1/user_downloads" +
                         $"?select=appid&downloaded_at=gte.{Uri.EscapeDataString(todayUtc)}";
            var req = new HttpRequestMessage(HttpMethod.Head, url);
            if (!string.IsNullOrEmpty(AppConfig.SupabaseAnonKey))
                req.Headers.TryAddWithoutValidation("apikey", AppConfig.SupabaseAnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetValidAccessTokenAsync());
            req.Headers.TryAddWithoutValidation("Prefer", "count=exact");

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;

            // Content-Range: "0-24/25"  (or "*/0" when empty). The count is after the slash.
            string? range = res.Content.Headers.TryGetValues("Content-Range", out var v) ? v.FirstOrDefault()
                          : (res.Headers.TryGetValues("Content-Range", out var hv) ? hv.FirstOrDefault() : null);
            int used = 0;
            if (range is not null && range.Split('/') is [_, var countStr] && int.TryParse(countStr, out int c))
                used = c;
            return new StandardUsage(used, AppConfig.DailyDownloadLimit);
        }
        catch
        {
            return null; // decorative, never block on it
        }
    }

    public async Task<SupporterStatus?> GetSupporterStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await SendAsync(HttpMethod.Get, "/api/me/supporter-status", ct);
            return await ReadJsonAsync<SupporterStatus>(res, ct);
        }
        catch { return null; }
    }

    public async Task<DlcInfo?> GetDlcInfoAsync(string appid, string baseAppId, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, $"/api/dlc/info?appid={appid}&base={baseAppId}", ct);
        return await ReadJsonAsync<DlcInfo>(res, ct);
    }

    public Task<DownloadedFile> DownloadManifestAsync(
        string appid, string source, string? gameName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string url = $"/api/manifest/download?appid={appid}&source={Uri.EscapeDataString(source)}";
        if (!string.IsNullOrEmpty(gameName)) url += $"&game_name={Uri.EscapeDataString(gameName)}";
        return DownloadFileAsync(url, $"{appid}.zip", progress, ct);
    }

    public async Task<bool> CheckDepotBoxManifestAvailabilityAsync(string appid, CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2.5));

            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/depotbox/api/games/{appid}/availability");
            if (_auth.IsSignedIn)
            {
                try
                {
                    string? token = await _auth.GetValidAccessTokenAsync();
                    if (!string.IsNullOrEmpty(token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                catch { }
            }

            var res = await _http.SendAsync(req, timeoutCts.Token);
            if (res.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(timeoutCts.Token), cancellationToken: timeoutCts.Token);
                if (doc.RootElement.TryGetProperty("available", out var avail) && avail.GetBoolean())
                    return true;
                if (doc.RootElement.TryGetProperty("success", out var succ) && succ.GetBoolean())
                {
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("availability", out var a))
                        return true;
                }
            }
        }
        catch { }

        // Resilient fallback: DepotBox is our #1 primary catalog with 155k+ games.
        // If the title is a valid Steam AppID and resolved in the app list or cached, mark it available.
        if (long.TryParse(appid, out long parsedAppId) && parsedAppId > 0)
        {
            if (_appList.GetName(parsedAppId) != null || _appInfo.GetCached(parsedAppId) != null)
                return true;
        }

        return false;
    }

    public async Task<DownloadedFile> DownloadDepotBoxManifestAsync(
        string appid, string? gameName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(new { appid }),
                System.Text.Encoding.UTF8,
                "application/json");
            return await DownloadPostFileAsync("/api/depotbox/api/direct-download", content, $"{appid}.zip", progress, ct);
        }
        catch (Exception ex)
        {
            PluginLog.Log($"DownloadDepotBoxManifestAsync appid={appid} direct-download failed: {ex.Message}. Falling back to /api/manifest/download");
            return await DownloadManifestAsync(appid, "DepotBox", gameName, progress, ct);
        }
    }

    /// <summary>
    /// Downloads manifest zip from Ryuu via auth worker secure_download endpoint (2nd priority).
    /// Uses server-side auth_code and user verification.
    /// </summary>
    public Task<DownloadedFile> DownloadRyuuManifestAsync(
        string appid, string? gameName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string url = $"/api/ryuu/secure_download?appid={Uri.EscapeDataString(appid)}";
        return DownloadFileAsync(url, $"{appid}.zip", progress, ct);
    }

    /// <summary>
    /// Downloads manifest zip directly from public Sushi manifest repository (3rd priority fallback).
    /// </summary>
    public Task<DownloadedFile> DownloadPublicSushiManifestAsync(
        string appid, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string url = $"https://proxy.netrunnergames.workers.dev/gh/https://raw.githubusercontent.com/sushi-dev55-alt/sushitools-games-repo-alt/refs/heads/main/{appid}.zip";
        return DownloadFromUrlAsync(url, $"{appid}.zip", progress, ct);
    }

    /// <summary>
    /// One depot's raw <c>.manifest</c> by id, so a depot download no longer depends on Steam happening
    /// to have the file in its depotcache.
    /// </summary>
    /// <remarks>
    /// Unlike every other endpoint here the ids go in the PATH, not the query string. Auth is the same
    /// Bearer token as the rest, and the response is raw bytes on 200 / a JSON error otherwise, which
    /// <see cref="SendAsync"/> already turns into an <see cref="ApiException"/>.
    ///
    /// <para>This one writes no history row and does NOT consume the daily download cap. It is instead
    /// limited to 120 requests per 10 minutes per user, and only cache misses count. A large game is
    /// ~20 depots, comfortably inside that — which is why manifests are fetched lazily per depot at
    /// download time rather than eagerly when the picker opens.</para>
    /// </remarks>
    public Task<DownloadedFile> DownloadDepotManifestAsync(
        long depotId, string manifestId,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
        => DownloadFileAsync($"/api/givemethemanifestpunk/{depotId}/{manifestId}",
                             $"{depotId}_{manifestId}.manifest", progress, ct);

    public Task<DownloadedFile> GenerateDlcAsync(
        string appid, string baseAppId, string? gameName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string url = $"/api/dlc/generate?appid={appid}&base={baseAppId}";
        if (!string.IsNullOrEmpty(gameName)) url += $"&game_name={Uri.EscapeDataString(gameName)}";
        return DownloadFileAsync(url, $"{appid}.lua", progress, ct);
    }

    // ── Denuvo fixes ────────────────────────────────────────────────

    private async Task<string> GetDepotBoxApiKeyAsync()
    {
        try
        {
            string token = await _auth.GetValidAccessTokenAsync();
            if (!string.IsNullOrWhiteSpace(token)) return token;
        }
        catch { }

        return AppConfig.ManifestBackendUserAgent;
    }

    // ── DepotBox game fixes ─────────────────────────────────────────

    /// <summary>DepotBox and Online-Fix game fixes endpoint. Returns game listings and tags (Bypass, Online Fix).</summary>
    public async Task<DenuvoListingsResponse?> GetDenuvoListingsAsync(CancellationToken ct = default)
    {
        try { await _appList.EnsureLoadedAsync(); } catch { }

        List<DenuvoGameListing> allGames = [];
        var tags = new List<DenuvoTag>
        {
            new DenuvoTag { Id = "bypass", Name = "Bypass", Slug = "bypass" },
            new DenuvoTag { Id = "online", Name = "Online Fix", Slug = "online" }
        };

        var endpoints = new[]
        {
            $"{AppConfig.ApiBaseUrl}/api/fixes/list",
            $"{AppConfig.ApiBaseUrl}/api/depotbox/api/game-fixes",
            "https://depotbox.org/api/game-fixes"
        };

        foreach (var endpoint in endpoints)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                using var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadAsStringAsync(ct);
                    var data = ParseDenuvoListings(json, _appList);
                    if (data?.Games is { Count: > 0 })
                    {
                        allGames.AddRange(data.Games);
                        if (data.Tags.Count > 0)
                        {
                            foreach (var t in data.Tags)
                            {
                                if (!tags.Any(x => string.Equals(x.Id, t.Id, StringComparison.OrdinalIgnoreCase)))
                                    tags.Add(t);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        var mergedGames = allGames
            .Where(g => !long.TryParse(g.AppId, out long aid) || !DenuvoService.BlockedAppIds.Contains(aid))
            .DistinctBy(g => g.AppId)
            .ToList();
        return new DenuvoListingsResponse { Games = mergedGames, Tags = tags };
    }

    private DenuvoListingsResponse? ParseDenuvoListings(string json, SteamAppListCache appList)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement arrayElem = default;

            if (root.ValueKind == JsonValueKind.Array)
            {
                arrayElem = root;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("games", out var g) && g.ValueKind == JsonValueKind.Array)
                    arrayElem = g;
                else if (root.TryGetProperty("fixes", out var f) && f.ValueKind == JsonValueKind.Array)
                    arrayElem = f;
                else if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
                    arrayElem = d;
                else if (root.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array)
                    arrayElem = r;
            }

            if (arrayElem.ValueKind != JsonValueKind.Array || arrayElem.GetArrayLength() == 0)
                return null;

            var dict = new Dictionary<string, DenuvoGameListing>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in arrayElem.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                string? appid = null;
                if (item.TryGetProperty("appid", out var ap)) appid = ap.ToString();
                else if (item.TryGetProperty("appId", out var ap2)) appid = ap2.ToString();
                else if (item.TryGetProperty("app_id", out var ap3)) appid = ap3.ToString();
                else if (item.TryGetProperty("id", out var idProp)) appid = idProp.ToString();

                if (string.IsNullOrWhiteSpace(appid)) continue;

                string? name = null;
                if (item.TryGetProperty("name", out var n)) name = n.GetString();
                else if (item.TryGetProperty("gameName", out var gn)) name = gn.GetString();
                else if (item.TryGetProperty("game_name", out var gn2)) name = gn2.GetString();
                else if (item.TryGetProperty("title", out var t)) name = t.GetString();

                if (!string.IsNullOrWhiteSpace(name) &&
                    (name.StartsWith("App ", StringComparison.OrdinalIgnoreCase) || long.TryParse(name.Trim(), out _)))
                {
                    name = null;
                }

                if (long.TryParse(appid, out long aid))
                {
                    string? cacheName = appList.GetName(aid) ?? _appInfo.GetCached(aid)?.Name;
                    if (!string.IsNullOrWhiteSpace(cacheName)) name = cacheName;
                }

                name ??= $"App {appid}";

                string? headerImage = null;
                if (item.TryGetProperty("header_image", out var hi)) headerImage = hi.GetString();
                else if (item.TryGetProperty("headerImage", out var hi2)) headerImage = hi2.GetString();
                else if (item.TryGetProperty("image", out var img)) headerImage = img.GetString();

                if (string.IsNullOrWhiteSpace(headerImage) && long.TryParse(appid, out long aidForImg))
                {
                    headerImage = SteamAppInfoCache.GuessHeaderImageUrl(aidForImg);
                }

                var itemTags = new List<DenuvoTag>();
                if (item.TryGetProperty("tags", out var tagsProp))
                {
                    if (tagsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tagElem in tagsProp.EnumerateArray())
                        {
                            if (tagElem.ValueKind == JsonValueKind.String)
                            {
                                string tStr = tagElem.GetString() ?? "";
                                if (!string.IsNullOrWhiteSpace(tStr) && !tStr.Contains("hypervisor", StringComparison.OrdinalIgnoreCase))
                                    itemTags.Add(new DenuvoTag { Id = tStr.ToLower(), Name = tStr.Equals("online", StringComparison.OrdinalIgnoreCase) ? "Online Fix" : tStr, Slug = tStr.ToLower() });
                            }
                            else if (tagElem.ValueKind == JsonValueKind.Object)
                            {
                                var tagObj = JsonSerializer.Deserialize<DenuvoTag>(tagElem.GetRawText(), JsonOpts);
                                if (tagObj is not null && !tagObj.Id.Contains("hypervisor", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (tagObj.Id.Equals("online", StringComparison.OrdinalIgnoreCase)) tagObj.Name = "Online Fix";
                                    itemTags.Add(tagObj);
                                }
                            }
                        }
                    }
                    else if (tagsProp.ValueKind == JsonValueKind.String)
                    {
                        string tStr = tagsProp.GetString() ?? "";
                        foreach (var part in tStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!part.Contains("hypervisor", StringComparison.OrdinalIgnoreCase))
                                itemTags.Add(new DenuvoTag { Id = part.ToLower(), Name = part.Equals("online", StringComparison.OrdinalIgnoreCase) ? "Online Fix" : part, Slug = part.ToLower() });
                        }
                    }
                }

                if (item.TryGetProperty("tag", out var singleTag) && singleTag.ValueKind == JsonValueKind.String)
                {
                    string tStr = singleTag.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(tStr) && !tStr.Contains("hypervisor", StringComparison.OrdinalIgnoreCase) && !itemTags.Any(t => string.Equals(t.Id, tStr, StringComparison.OrdinalIgnoreCase)))
                    {
                        itemTags.Add(new DenuvoTag { Id = tStr.ToLower(), Name = tStr.Equals("online", StringComparison.OrdinalIgnoreCase) ? "Online Fix" : tStr, Slug = tStr.ToLower() });
                    }
                }

                if (itemTags.Count == 0)
                {
                    itemTags.Add(new DenuvoTag { Id = "bypass", Name = "Bypass", Slug = "bypass" });
                }

                if (!dict.TryGetValue(appid, out var listing))
                {
                    listing = new DenuvoGameListing
                    {
                        AppId = appid,
                        Name = name,
                        HeaderImage = headerImage,
                        FixCount = 1,
                        Tags = itemTags
                    };
                    dict[appid] = listing;
                }
                else
                {
                    listing.FixCount++;
                    foreach (var t in itemTags)
                    {
                        if (!listing.Tags.Any(x => string.Equals(x.Id, t.Id, StringComparison.OrdinalIgnoreCase)))
                            listing.Tags.Add(t);
                    }
                }
            }

            if (dict.Count > 0)
            {
                var responseTags = new List<DenuvoTag>
                {
                    new DenuvoTag { Id = "bypass", Name = "Bypass", Slug = "bypass" },
                    new DenuvoTag { Id = "online", Name = "Online Fix", Slug = "online" }
                };
                return new DenuvoListingsResponse { Games = dict.Values.ToList(), Tags = responseTags };
            }
        }
        catch { }
        return null;
    }

    /// <summary>Fetches a game's fixes from DepotBox /api/game-fixes?q=appid.</summary>
    public async Task<DenuvoFixesResponse?> GetDenuvoFixesAsync(string appid, CancellationToken ct = default)
    {
        string? name = _appList.GetName(long.TryParse(appid, out long aid) ? aid : 0);
        string? headerImage = null;
        List<DenuvoFix> allFixes = [];

        var endpoints = new[]
        {
            $"{AppConfig.ApiBaseUrl}/api/depotbox/api/game-fixes?q={Uri.EscapeDataString(appid)}",
            $"https://depotbox.org/api/game-fixes?q={Uri.EscapeDataString(appid)}"
        };

        foreach (var endpoint in endpoints)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                using var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadAsStringAsync(ct);
                    var data = ParseDenuvoFixes(json, appid);
                    if (data is not null)
                    {
                        if (!string.IsNullOrEmpty(data.Name) && data.Name != appid) name ??= data.Name;
                        if (!string.IsNullOrEmpty(data.HeaderImage)) headerImage ??= data.HeaderImage;
                        if (data.Fixes is { Count: > 0 })
                        {
                            allFixes.AddRange(data.Fixes);
                        }
                    }
                }
            }
            catch { }
        }

        var mergedFixes = allFixes.DistinctBy(f => f.Id).ToList();
        if (mergedFixes.Count == 0)
        {
            return CreateFallbackFixesResponse(appid, name);
        }

        return new DenuvoFixesResponse
        {
            AppId = appid,
            Name = name ?? appid,
            HeaderImage = headerImage ?? (long.TryParse(appid, out long parsedId) ? SteamAppInfoCache.GuessHeaderImageUrl(parsedId) : null),
            Fixes = mergedFixes
        };
    }

    public static DenuvoFixesResponse CreateFallbackFixesResponse(string appid, string? gameName)
    {
        long.TryParse(appid, out long aid);
        string name = gameName ?? (aid > 0 ? $"App {aid}" : appid);
        string headerImage = aid > 0 ? SteamAppInfoCache.GuessHeaderImageUrl(aid) : "";
        string cleanName = name.Replace(' ', '_');

        return new DenuvoFixesResponse
        {
            AppId = appid,
            Name = name,
            HeaderImage = headerImage,
            Fixes = new List<DenuvoFix>
            {
                new DenuvoFix
                {
                    Id = $"{appid}_bypass",
                    Title = $"{name} Bypass Fix",
                    Description = "Bypass game fix release for this title.",
                    HasManifest = true,
                    HasFix = true,
                    ManifestFilename = $"{appid}.zip",
                    FixFilename = $"{cleanName}_bypass.zip",
                    Tags = new List<DenuvoTag>
                    {
                        new DenuvoTag { Id = "bypass", Name = "Bypass", Slug = "bypass" }
                    }
                }
            }
        };
    }

    private static DenuvoFixesResponse? ParseDenuvoFixes(string json, string appid)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("fixes", out var fixesProp) && fixesProp.ValueKind == JsonValueKind.Array)
                {
                    var fixes = JsonSerializer.Deserialize<List<DenuvoFix>>(fixesProp.GetRawText(), JsonOpts) ?? [];
                    string? name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? img = root.TryGetProperty("header_image", out var hi) ? hi.GetString() : null;
                    return new DenuvoFixesResponse { AppId = appid, Name = name ?? appid, HeaderImage = img, Fixes = fixes };
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                var fixes = JsonSerializer.Deserialize<List<DenuvoFix>>(json, JsonOpts);
                if (fixes is { Count: > 0 })
                    return new DenuvoFixesResponse { AppId = appid, Name = appid, HeaderImage = null, Fixes = fixes };
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Checks whether a game has "Online Fix" and/or "Bypass" fixes available.
    /// Queries /api/game-fixes (proxied via worker with DepotBox API key) and checks the online-fix scraper.
    /// </summary>
    public async Task<(bool HasBypass, bool HasOnlineFix)> CheckGameFixBadgesAsync(
        string appid, string? gameName = null, CancellationToken ct = default)
    {
        bool hasBypass = false;
        bool hasOnline = false;

        if (string.IsNullOrWhiteSpace(appid)) return (false, false);

        var endpoints = new[]
        {
            $"{AppConfig.ApiBaseUrl}/api/game-fixes?tag=bypass,online&q={Uri.EscapeDataString(appid)}",
            $"{AppConfig.ApiBaseUrl}/api/depotbox/api/game-fixes?tag=bypass,online&q={Uri.EscapeDataString(appid)}"
        };

        foreach (var endpoint in endpoints)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                if (_auth.IsSignedIn)
                {
                    try
                    {
                        string? token = await _auth.GetValidAccessTokenAsync();
                        if (!string.IsNullOrEmpty(token))
                            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    }
                    catch { }
                }

                using var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode)
                {
                    string json = await res.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    void CheckElementTags(JsonElement elem)
                    {
                        if (elem.TryGetProperty("tags", out var tagsProp))
                        {
                            if (tagsProp.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var t in tagsProp.EnumerateArray())
                                {
                                    string? id = null;
                                    if (t.ValueKind == JsonValueKind.String) id = t.GetString();
                                    else if (t.ValueKind == JsonValueKind.Object)
                                    {
                                        if (t.TryGetProperty("id", out var idProp)) id = idProp.GetString();
                                        else if (t.TryGetProperty("slug", out var sProp)) id = sProp.GetString();
                                        else if (t.TryGetProperty("name", out var nProp)) id = nProp.GetString();
                                    }
                                    if (string.Equals(id, "bypass", StringComparison.OrdinalIgnoreCase)) hasBypass = true;
                                    if (string.Equals(id, "online", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(id, "onlinefix", StringComparison.OrdinalIgnoreCase)) hasOnline = true;
                                }
                            }
                            else if (tagsProp.ValueKind == JsonValueKind.String)
                            {
                                string str = tagsProp.GetString() ?? "";
                                if (str.Contains("bypass", StringComparison.OrdinalIgnoreCase)) hasBypass = true;
                                if (str.Contains("online", StringComparison.OrdinalIgnoreCase)) hasOnline = true;
                            }
                        }
                    }

                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        CheckElementTags(root);
                        if (root.TryGetProperty("fixes", out var fixesProp) && fixesProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var f in fixesProp.EnumerateArray())
                            {
                                CheckElementTags(f);
                            }
                        }
                        if (root.TryGetProperty("games", out var gamesProp) && gamesProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var g in gamesProp.EnumerateArray())
                            {
                                CheckElementTags(g);
                            }
                        }
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in root.EnumerateArray())
                        {
                            CheckElementTags(item);
                        }
                    }

                    if (hasBypass || hasOnline) break;
                }
            }
            catch { }
        }

        return (hasBypass, hasOnline);
    }

    /// <summary>Downloads a fix archive from DepotBox /api/game-fixes/download or a manifest zip.</summary>
    public async Task<DownloadedFile> DownloadDenuvoAsync(
        string fixId, string slot, string fallbackName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        long.TryParse(fixId.Split('_')[0], out long parsedAppId);
        string effectiveAppId = parsedAppId > 0 ? parsedAppId.ToString() : fixId;
        if (slot == "manifest")
        {
            string manifestUrl = $"{AppConfig.ApiBaseUrl}/api/manifest/download?appid={Uri.EscapeDataString(effectiveAppId)}";
            return await DownloadFileAsync(manifestUrl, $"{effectiveAppId}.zip", progress, ct);
        }

        // Scraped online-fix.me fixes: direct hoster download, no depotbox/worker involvement.
        if (fixId.Contains("_ofme_", StringComparison.Ordinal))
            return await DownloadOnlineFixAsync(parsedAppId, fallbackName, progress, ct);

        string proxyUrl = $"{AppConfig.ApiBaseUrl}/api/depotbox/api/game-fixes/download?id={Uri.EscapeDataString(fixId)}&file={Uri.EscapeDataString(fallbackName)}&appid={parsedAppId}";
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, proxyUrl);
            string token = await _auth.GetValidAccessTokenAsync();
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            string? uid = _auth.DiscordId ?? _auth.UserId;
            if (!string.IsNullOrEmpty(uid))
                req.Headers.TryAddWithoutValidation("X-Discord-Id", uid);

            var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.IsSuccessStatusCode)
            {
                return await HttpFileDownloader.SaveResponseAsync(res, fallbackName, progress, ct);
            }
        }
        catch { }

        // Direct fallback endpoint if worker is unreachable
        string directUrl = $"https://depotbox.org/api/game-fixes/download?id={Uri.EscapeDataString(fixId)}&file={Uri.EscapeDataString(fallbackName)}";
        return await DownloadFromUrlAsync(directUrl, fallbackName, progress, ct);
    }

    // ── Online-fix.me scraping (fixes tagged "online") ───────────────

    private const string OnlineFixReferer = "https://online-fix.me/";
    private const string OnlineFixUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly object _ofLock = new();
    private readonly Dictionary<long, OnlineFixScrapeResult?> _ofCache = new();

    public sealed record OnlineFixScrapeResult(
        string SearchName, string Title, string ArticleUrl, string VersionStr, string? DirectFixUrl, string? FixFileName);

    /// <summary>
    /// Finds the game's article on online-fix.me and scrapes its game version + direct fix download link.
    /// Result is cached per appid for the session (including negative results).
    /// </summary>
    public async Task<OnlineFixScrapeResult?> ScrapeOnlineFixForGameAsync(long appId, string gameName, CancellationToken ct = default)
    {
        if (appId <= 0) return null;
        lock (_ofLock) { if (_ofCache.TryGetValue(appId, out var hit)) return hit; }

        OnlineFixScrapeResult? result = null;
        try
        {
            string name = string.IsNullOrWhiteSpace(gameName) ? $"App {appId}" : gameName;
            string? articleUrl = await SearchOnlineFixArticleAsync(name, ct);
            if (articleUrl is not null)
                result = await ScrapeOnlineFixArticleAsync(articleUrl, name, ct);
        }
        catch { }

        lock (_ofLock) _ofCache[appId] = result;
        return result;
    }

    private async Task<string?> SearchOnlineFixArticleAsync(string gameName, CancellationToken ct)
    {
        string url = $"https://online-fix.me/index.php?do=search&subaction=search&story={Uri.EscapeDataString(gameName)}";
        string html = await GetOnlineFixHtmlAsync(url, ct);
        if (html.Length == 0) return null;

        var match = Regex.Match(html, "href=\"(?:https?://online-fix\\.me)?(/games/[^\"]+\\.html)\"", RegexOptions.IgnoreCase);
        return match.Success ? $"https://online-fix.me{match.Groups[1].Value}" : null;
    }

    private async Task<OnlineFixScrapeResult?> ScrapeOnlineFixArticleAsync(string articleUrl, string searchName, CancellationToken ct)
    {
        string html = await GetOnlineFixHtmlAsync(articleUrl, ct);
        if (html.Length == 0) return null;

        // "Игра через: Steam" is usable; "Microsoft Store" games need GDK fixes — never list those.
        var platformMatch = Regex.Match(html, "Игра через:\\s*(?:<[^>]+>\\s*)*([А-Яа-яЁёA-Za-z ]+)", RegexOptions.IgnoreCase);
        if (platformMatch.Success)
        {
            string platform = platformMatch.Groups[1].Value.Trim();
            if (platform.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                platform.Contains("Store", StringComparison.OrdinalIgnoreCase))
                return null;
        }

        string title = "";
        var titleMatch = Regex.Match(html, "<h1[^>]*>(?:\\s*<[^>]+>)*([^<]+)", RegexOptions.IgnoreCase);
        if (titleMatch.Success) title = titleMatch.Groups[1].Value.Trim();

        string versionStr = "";
        var m = Regex.Match(html, "<b>\\s*(?:Game version|Версия игры|Версия):\\s*([^<]+)</b>", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(html, "<b>\\s*(Build[^<]+)</b>", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(html, "Версия игры:\\s*([0-9a-zA-Z\\._\\-]+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            string raw = m.Groups[1].Value.Trim();
            versionStr = Regex.IsMatch(raw, "^(?:Game version|Версия игры|Версия):", RegexOptions.IgnoreCase)
                ? Regex.Replace(raw, "^(?:Версия игры|Версия):", "Game version:", RegexOptions.IgnoreCase).Trim()
                : $"Game version: {raw}";
        }

        string? direct = null, fileName = null;
        var hosterMatch = Regex.Match(html, "href=\"(https://hosters\\.online-fix\\.me:2053/[^\"]+)\"", RegexOptions.IgnoreCase);
        if (hosterMatch.Success)
        {
            string hosterUrl = hosterMatch.Groups[1].Value.Replace("&amp;", "&");
            string hosterHtml = await GetOnlineFixHtmlAsync(hosterUrl, ct);
            hosterHtml = hosterHtml.Replace("&quot;", "\"").Replace("&amp;", "&");

            // The fix archive may only be mirrored on one of the hosts: collect candidates from every
            // data-links block, drop GDK (Microsoft Store) helper files, then pick by priority.
            var candidates = new List<(string FileName, string DirectLink)>();
            foreach (Match lm in Regex.Matches(hosterHtml, "data-links=['\"](\\[\\{[\\s\\S]*?\\}\\])['\"]", RegexOptions.IgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(lm.Groups[1].Value);
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        string? fn = item.TryGetProperty("file_name", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                        string? dl = item.TryGetProperty("direct_link", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                        if (string.IsNullOrEmpty(fn) || string.IsNullOrEmpty(dl)) continue;
                        if (fn.ToLowerInvariant().Contains("gdkhelper")) continue; // MS Store fix — needs the whole game, not usable on Steam
                        candidates.Add((fn, dl));
                    }
                }
                catch { }
            }

            // Prefer an explicit fix/repair archive, then the OFME release parts, then anything.
            string? chosen = candidates.FirstOrDefault(c => c.FileName.ToLowerInvariant().Contains("fix_repair")).FileName
                ?? candidates.FirstOrDefault(c => { string l = c.FileName.ToLowerInvariant(); return l.Contains("fix") && !l.Contains("ofme"); }).FileName
                ?? candidates.FirstOrDefault(c => c.FileName.ToLowerInvariant().Contains("ofme")).FileName
                ?? candidates.FirstOrDefault().FileName;

            if (chosen is not null)
            {
                fileName = chosen;
                string dl = candidates.First(c => c.FileName == chosen).DirectLink;
                direct = dl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? dl
                    : $"https://hosters.online-fix.me:2053/{dl.TrimStart('/')}";
            }
        }

        if (string.IsNullOrEmpty(title))
        {
            title = Uri.UnescapeDataString(articleUrl.Split('/').LastOrDefault() ?? "Online Fix");
            title = Regex.Replace(title, "^\\d+-", "").Replace(".html", "").Replace('-', ' ').Trim();
        }

        return new OnlineFixScrapeResult(searchName, title, articleUrl, versionStr, direct, fileName);
    }

    private async Task<string> GetOnlineFixHtmlAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(OnlineFixUserAgent);
            req.Headers.Referrer = new Uri(OnlineFixReferer);
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return "";
            byte[] bytes = await res.Content.ReadAsByteArrayAsync(ct);
            try { return Encoding.GetEncoding(1251).GetString(bytes); }
            catch { return Encoding.UTF8.GetString(bytes); }
        }
        catch { return ""; }
    }

    /// <summary>Downloads the scraped online-fix.me direct link (with the UA/Referer the hoster requires).</summary>
    private async Task<DownloadedFile> DownloadOnlineFixAsync(
        long appId, string fallbackName, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        OnlineFixScrapeResult? scrape;
        lock (_ofLock) _ofCache.TryGetValue(appId, out scrape);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            string searchName = scrape?.SearchName ?? $"App {appId}";
            scrape ??= await ScrapeOnlineFixForGameAsync(appId, searchName, ct);
            if (scrape?.DirectFixUrl is not { } url)
                throw new InvalidOperationException($"No online-fix.me fix link found for '{searchName}'");

            string fileName = string.IsNullOrWhiteSpace(scrape.FixFileName) ? fallbackName : scrape.FixFileName!;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(OnlineFixUserAgent);
                req.Headers.Referrer = new Uri(OnlineFixReferer);
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (res.IsSuccessStatusCode)
                    return await HttpFileDownloader.SaveResponseAsync(res, fileName, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            // Link likely expired: drop the cache and re-scrape once.
            lock (_ofLock) _ofCache.Remove(appId);
            scrape = null;
        }

        throw new InvalidOperationException("Online-fix.me download failed");
    }

    // ── Plumbing ────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        // All callers of SendAsync are login-gated endpoints; the caller ensures the user is signed in.
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetValidAccessTokenAsync());

        var res = await _http.SendAsync(req, completion, ct);
        if (res.IsSuccessStatusCode) return res;

        string message = string.Format(Resources.Strings.Api_Err_RequestFailed, (int)res.StatusCode);
        try
        {
            var err = JsonSerializer.Deserialize<ApiError>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (!string.IsNullOrWhiteSpace(err?.Error)) message = err.Error;
        }
        catch { }

        if (res.StatusCode == HttpStatusCode.Unauthorized) message = Resources.Strings.Api_Err_SessionExpired;
        throw new ApiException(message, res.StatusCode);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct), JsonOpts);

    private async Task<DownloadedFile> DownloadFileAsync(
        string url, string fallbackName, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Get, url, ct, HttpCompletionOption.ResponseHeadersRead);
        return await HttpFileDownloader.SaveResponseAsync(res, fallbackName, progress, ct);
    }

    private async Task<DownloadedFile> DownloadPostFileAsync(
        string url, HttpContent content, string fallbackName, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        string? token = await _auth.GetValidAccessTokenAsync();
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            string message = string.Format(Resources.Strings.Api_Err_RequestFailed, (int)res.StatusCode);
            try
            {
                var err = JsonSerializer.Deserialize<ApiError>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
                if (!string.IsNullOrWhiteSpace(err?.Error)) message = err.Error;
            }
            catch { }
            throw new ApiException(message, res.StatusCode);
        }
        return await HttpFileDownloader.SaveResponseAsync(res, fallbackName, progress, ct);
    }

    /// <summary>Download a file from an absolute URL with NO auth header (e.g. a signed R2 link).</summary>
    private async Task<DownloadedFile> DownloadFromUrlAsync(
        string url, string fallbackName, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // New request (not via SendAsync) so no Bearer header and the absolute URL isn't prefixed.
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new ApiException(string.Format(Resources.Strings.Api_Err_DownloadFailed, (int)res.StatusCode), res.StatusCode);
        return await HttpFileDownloader.SaveResponseAsync(res, fallbackName, progress, ct);
    }
}
