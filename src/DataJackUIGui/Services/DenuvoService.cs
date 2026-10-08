using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataJackUIGui.Services;

/// <summary>
/// Service for detecting whether a Steam game uses Denuvo Anti-Tamper DRM.
/// Tracks both active Denuvo titles and games from which Denuvo was formerly removed (to avoid false positives).
/// Seeds from a verified baseline database, loads cached IDs from cache.json, and synchronizes
/// in the background via the Cloudflare Worker API or Steam Curator.
/// </summary>
public class DenuvoService
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private readonly CacheService _cache;
    private readonly HashSet<long> _activeIds;
    private readonly HashSet<long> _removedIds;
    private Task? _syncTask;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Explicitly blocked non-game / false-positive AppIDs (e.g. SteamVR runtime).
    /// </summary>
    public static readonly HashSet<long> BlockedAppIds = [250820];

    /// <summary>
    /// Verified active Denuvo Steam AppIDs (baseline set derived from PCGamingWiki and Steam Curator).
    /// </summary>
    public static readonly HashSet<long> BaselineActiveIds =
    [
        234140, 265300, 285190, 287700, 304390, 312650, 312660, 312670, 315210,
        321960, 337000, 356190, 364360, 368260, 371660, 374520, 375910, 391040, 408250,
        410570, 420560, 427100, 429660, 436780, 438490, 445310, 446560, 447040, 456610,
        458770, 460870, 460930, 465280, 466130, 470220, 482730, 482750, 488790, 491540,
        493340, 515220, 517630, 526980, 530070, 532210, 543460, 546050, 552520, 562810,
        574050, 579820, 582160, 585080, 592580, 592600, 594570, 609150, 621830, 624090,
        624120, 626690, 629820, 637100, 637650, 641080, 648350, 650070, 650510, 678950,
        678960, 694280, 700600, 703080, 714370, 719950, 737800, 738530, 742120, 752480,
        770240, 775430, 785260, 790540, 790820, 801800, 809890, 812140, 845070, 872790,
        872820, 916440, 928600, 939960, 950050, 953580, 990080, 996470, 1029690, 1038250,
        1064070, 1088850, 1099410, 1100600, 1113000, 1134570, 1142710, 1222680, 1222690, 1222730,
        1225560, 1225570, 1225580, 1225590, 1233570, 1235140, 1237320, 1237950, 1237980, 1238080,
        1238810, 1238840, 1238880, 1239520, 1244460, 1252330, 1259970, 1262540, 1262580, 1263850,
        1263860, 1273400, 1282590, 1285190, 1294810, 1295660, 1307710, 1313860, 1328660, 1357840,
        1364780, 1382330, 1413480, 1451190, 1462570, 1487210, 1490890, 1506830, 1517290, 1519350,
        1569040, 1570010, 1602010, 1611910, 1649080, 1665460, 1677350, 1687950, 1692250, 1693980,
        1708520, 1761390, 1777620, 1785650, 1794960, 1795190, 1805480, 1808700, 1809700, 1810820,
        1811260, 1844380, 1846380, 1849250, 1868170, 1868180, 1875830, 1904540, 1938010, 1941540,
        1944790, 1963680, 1971870, 2022670, 2051010, 2055290, 2058030, 2058180, 2058190, 2072450,
        2096600, 2096610, 2108330, 2161700, 2169200, 2185060, 2208920, 2215200, 2215260, 2221920,
        2231380, 2239550, 2244210, 2246340, 2252570, 2254740, 2287220, 2288340, 2288350, 2338770,
        2358720, 2361770, 2369390, 2375550, 2379390, 2383760, 2385530, 2395210, 2424110, 2486820,
        2488620, 2495100, 2513280, 2556990, 2582560, 2591280, 2604480, 2624870, 2638890, 2649560,
        2669320, 2679460, 2680010, 2688950, 2741360, 2750080, 2751000, 2840770, 2842040, 2852190,
        2853730, 2878600, 2878980, 2924540, 2928600, 2929170, 2958130, 2963950, 3010270, 3035570,
        3059520, 3061810, 3094260, 3159330, 3219030, 3230400, 3259780, 3274580, 3321460, 3357650,
        3405690, 3472040, 3483510, 3489700, 3551340, 3558670, 3603000, 3621390, 3681610, 3717070,
        3751950, 3764200, 3809850, 3936610, 3937550, 4013150, 4078430, 4080220, 4084710, 4115450,
        4128400, 4148530, 4260840, 4354570
    ];

    /// <summary>
    /// Games that formerly used Denuvo but had it officially or silently removed.
    /// Overrides outdated Steam store notices to ensure users are not overcharged.
    /// </summary>
    private static readonly HashSet<long> BaselineRemovedIds =
    [
        208650, 221680, 223100, 225540, 236870, 300060, 304430, 304530, 350640, 368070,
        379720, 384190, 389730, 391220, 397540, 401760, 403640, 412020, 417430, 418370,
        435100, 440900, 461870, 464340, 480490, 493200, 493840, 494670, 501590, 502500,
        515180, 524220, 534380, 535930, 544750, 548570, 554620, 579020, 582010, 584400,
        595520, 601150, 614570, 627270, 633230, 638970, 668580, 712100, 741820, 742300,
        750130, 750920, 760060, 779340, 782330, 834280, 863550, 883710, 921570, 924970,
        924980, 952060, 952070, 960910, 960990, 976310, 976590, 989690, 997070, 1004640,
        1012840, 1030840, 1056960, 1072420, 1080110, 1113560, 1113570, 1135300, 1172380, 1179580,
        1190460, 1196590, 1222140, 1222700, 1237970, 1238000, 1277400, 1286680, 1295510, 1324130,
        1328670, 1341050, 1345890, 1358750, 1399080, 1446650, 1446780, 1451810, 1454970, 1475810,
        1485590, 1496790, 1544020, 1577120, 1588010, 1627720, 1677280, 1680880, 1715130, 1748660,
        1774580, 1776380, 1798010, 1798020, 1817230, 1840080, 1850510, 1874000, 1890740, 1895810,
        1909950, 1937780, 1963210, 1967430, 1971650, 2009100, 2014380, 2050650, 2054970, 2078040,
        2109370, 2187220, 2316580, 2356560, 2374190, 2401970, 2455640, 2456740, 2490990, 2510710,
        2527390, 2631250, 2701660, 2844850, 2893570, 3017860
    ];

    public DenuvoService(CacheService cache)
    {
        _cache = cache;
        _activeIds = [.. BaselineActiveIds, .. cache.GetDenuvoActiveAppIds()];
        _activeIds.ExceptWith(BlockedAppIds);
        _removedIds = [.. BaselineRemovedIds, .. cache.GetDenuvoRemovedAppIds()];
    }

    /// <summary>
    /// Synchronous check against the in-memory Denuvo set. Allocation-free and 0ms latency.
    /// Checks removed list first to ensure stripped titles never trigger Denuvo pricing.
    /// </summary>
    public bool IsDenuvo(long appId)
    {
        if (BlockedAppIds.Contains(appId) || _removedIds.Contains(appId)) return false;
        return _activeIds.Contains(appId);
    }

    /// <summary>
    /// Checks whether an AppID is in the verified Denuvo-removed list.
    /// </summary>
    public bool IsDenuvoRemoved(long appId) => _removedIds.Contains(appId);

    /// <summary>
    /// Synchronizes the Denuvo AppID database with edge API / Steam Curator in the background if stale.
    /// </summary>
    public Task EnsureFreshAsync() => _syncTask ??= RefreshIfStaleAsync();

    private async Task RefreshIfStaleAsync()
    {
        try
        {
            long lastMs = _cache.GetDenuvoAppIdsFetchedAt();
            if (lastMs > 0 && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(lastMs) < MaxAge)
            {
                return; // cache still fresh
            }

            // 1. Try centralized edge API first (fastest and pre-compiled)
            try
            {
                string edgeUrl = $"{AppConfig.ApiBaseUrl}/api/denuvo/list";
                using var req = new HttpRequestMessage(HttpMethod.Get, edgeUrl);
                using var res = await Http.SendAsync(req);
                if (res.IsSuccessStatusCode)
                {
                    string json = await res.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var newActive = new HashSet<long>();
                    var newRemoved = new HashSet<long>();

                    if (root.TryGetProperty("active", out var actEl) && actEl.ValueKind == JsonValueKind.Array)
                        foreach (var el in actEl.EnumerateArray()) if (el.TryGetInt64(out long id)) newActive.Add(id);

                    if (root.TryGetProperty("removed", out var remEl) && remEl.ValueKind == JsonValueKind.Array)
                        foreach (var el in remEl.EnumerateArray()) if (el.TryGetInt64(out long id)) newRemoved.Add(id);

                    if (newActive.Count > 0 || newRemoved.Count > 0)
                    {
                        lock (_activeIds)
                        {
                            foreach (var id in newActive) _activeIds.Add(id);
                            foreach (var id in newRemoved) { _removedIds.Add(id); _activeIds.Remove(id); }
                        }
                        _cache.SaveDenuvoAppIds(_activeIds, _removedIds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        return;
                    }
                }
            }
            catch { }

            // 2. Fallback: Query Steam Curator 26095454 (Denuvo Games) directly
            var fetched = new HashSet<long>();
            for (int start = 0; start < 400; start += 50)
            {
                string url = $"https://store.steampowered.com/curator/26095454/ajaxgetfilteredrecommendations/render/?query=&start={start}&count=50";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "DataJackUI (https://netrunner.games)");
                using var res = await Http.SendAsync(req);
                if (!res.IsSuccessStatusCode) break;

                string json = await res.Content.ReadAsStringAsync();
                var matches = Regex.Matches(json, @"(?s)<div[^>]*class=""recommendation"".*?data-ds-appid=""(\d+)"".*?recommendation_type_ctn.*?<span class='([^']+)'");
                if (matches.Count == 0) break;

                foreach (Match m in matches)
                {
                    if (long.TryParse(m.Groups[1].Value, out long id))
                    {
                        string badgeClass = m.Groups[2].Value;
                        if (badgeClass == "color_not_recommended")
                        {
                            if (!_removedIds.Contains(id)) fetched.Add(id);
                        }
                    }
                }
            }

            if (fetched.Count > 0)
            {
                lock (_activeIds)
                {
                    foreach (var id in fetched) _activeIds.Add(id);
                }
                _cache.SaveDenuvoAppIds(_activeIds, _removedIds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }
        catch
        {
            // Best effort: fallback seamlessly to seeded baseline and cached database
        }
    }
}
