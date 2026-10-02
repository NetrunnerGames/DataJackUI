using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DataJackUIGui.Models;
using DataJackUIGui.Services.Downloads;

namespace DataJackUIGui.Services;

public class ApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public record DownloadedFile(string FilePath, string FileName);

/// <summary>Typed client for the lua.tools web API, authenticated with a Supabase bearer token.</summary>
public class DataJackUIApiClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly AuthService _auth;
    private readonly SteamAppInfoCache _appInfo;
    private readonly SteamAppListCache _appList;
    private readonly CoverCache _covers;
    private readonly SettingsService _settings;

    internal DataJackUIApiClient(HttpClient http, AuthService auth, SteamAppInfoCache appInfo, SteamAppListCache appList, CoverCache covers, SettingsService settings)
    {
        _http = http;
        _auth = auth;
        _appInfo = appInfo;
        _appList = appList;
        _covers = covers;
        _settings = settings;
    }

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

        try
        {
            await _appList.EnsureLoadedAsync();
        }
        catch { /* best effort */ }

        var localResults = _appList.Search(query, maxResults: 12);

        List<SteamSearchResult> storeResults = [];
        var url = $"{AppConfig.SteamStoreSearchUrl}?term={Uri.EscapeDataString(query)}&l=english&cc=US";
        try
        {
            var res = await _http.GetAsync(url, ct);
            if (res.IsSuccessStatusCode)
            {
                var data = await ReadJsonAsync<SteamStoreSearchResponse>(res, ct);
                storeResults = (data?.Items ?? []).Take(8).Select(i => new SteamSearchResult { AppId = i.Id, Name = i.Name }).ToList();
            }
        }
        catch { }

        var combined = localResults
            .Concat(storeResults)
            .DistinctBy(r => r.AppId)
            .Take(12)
            .ToList();

        return combined;
    }

    /// <summary>Steam's top sellers (from global charts) and new releases lists for the Add page strips.
    /// Public, no auth required. Uses Cloudflare CDN for images.</summary>
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

    /// <summary>Source name → "available" | "unavailable" | other status.</summary>
    public async Task<Dictionary<string, string>> CheckSourcesAsync(string appid, CancellationToken ct = default)
    {
        // Calls the manifest backend directly (no lua.tools, no auth). The backend is gated
        // by a fixed User-Agent rather than a token, so guests can check availability.
        var req = new HttpRequestMessage(HttpMethod.Get, $"{AppConfig.ManifestBackendUrl}/check_apis?appid={appid}");
        req.Headers.TryAddWithoutValidation("User-Agent", AppConfig.ManifestBackendUserAgent);
        var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return [];
        return await ReadJsonAsync<Dictionary<string, string>>(res, ct) ?? [];
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

    // ── DepotBox game fixes ─────────────────────────────────────────

    /// <summary>DepotBox game fixes endpoint. Returns game listings and the 3 DepotBox tags (Bypass, Online, Hypervisor).</summary>
    public async Task<GameFixListingsResponse?> GetGameFixListingsAsync(CancellationToken ct = default)
    {
        List<GameFixListing> allGames = [];
        var tags = new List<GameFixTag>
        {
            new GameFixTag { Id = "bypass", Name = "Bypass", Slug = "bypass" },
            new GameFixTag { Id = "online", Name = "Online", Slug = "online" },
            new GameFixTag { Id = "hypervisor", Name = "Hypervisor", Slug = "hypervisor" }
        };

        var endpoints = new[]
        {
            $"{AppConfig.DepotBoxProxyUrl}/api/game-fixes?tag=bypass,online,hypervisor",
            "https://depotbox.org/api/game-fixes?tag=bypass,online,hypervisor",
            "https://depotbox.pages.dev/api/game-fixes?tag=bypass,online,hypervisor"
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
                    var data = ParseGameFixListings(json);
                    if (data?.Games is { Count: > 0 })
                    {
                        allGames.AddRange(data.Games);
                        break;
                    }
                }
            }
            catch { }
        }

        var mergedGames = allGames
            .Where(g => !string.IsNullOrWhiteSpace(g.AppId) || !string.IsNullOrWhiteSpace(g.Name))
            .GroupBy(g => !string.IsNullOrWhiteSpace(g.AppId) ? g.AppId : g.Name)
            .Select(group =>
            {
                var first = group.First();
                int fixCount = group.Sum(g => g.Fixes.Count > 0 ? g.Fixes.Count : g.FixCount);
                first.FixCount = fixCount;
                var allTags = group.SelectMany(g => g.Tags).DistinctBy(t => t.Id.ToLowerInvariant()).ToList();
                if (allTags.Count > 0) first.Tags = allTags;
                return first;
            })
            .ToList();

        return new GameFixListingsResponse { Games = mergedGames, Tags = tags };
    }

    internal static GameFixListingsResponse? ParseGameFixListings(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var games = new List<GameFixListing>();

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("games", out var gamesProp) && gamesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in gamesProp.EnumerateArray())
                        games.Add(ParseGameFixListingElement(elem));
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in root.EnumerateArray())
                    games.Add(ParseGameFixListingElement(elem));
            }

            if (games.Count > 0)
            {
                var tags = new List<GameFixTag>
                {
                    new GameFixTag { Id = "bypass", Name = "Bypass", Slug = "bypass" },
                    new GameFixTag { Id = "online", Name = "Online", Slug = "online" },
                    new GameFixTag { Id = "hypervisor", Name = "Hypervisor", Slug = "hypervisor" }
                };
                return new GameFixListingsResponse { Games = games, Tags = tags };
            }
        }
        catch { }
        return null;
    }

    private static GameFixListing ParseGameFixListingElement(JsonElement elem)
    {
        var item = new GameFixListing();

        if (elem.TryGetProperty("appid", out var ap)) item.AppId = ap.ToString();
        else if (elem.TryGetProperty("app_id", out var ap2)) item.AppId = ap2.ToString();
        else if (elem.TryGetProperty("appId", out var ap3)) item.AppId = ap3.ToString();
        else if (elem.TryGetProperty("id", out var ap4) && long.TryParse(ap4.ToString(), out _)) item.AppId = ap4.ToString();

        if (elem.TryGetProperty("name", out var np) && !string.IsNullOrWhiteSpace(np.GetString())) item.Name = np.GetString()!;
        else if (elem.TryGetProperty("title", out var np2) && !string.IsNullOrWhiteSpace(np2.GetString())) item.Name = np2.GetString()!;
        else if (elem.TryGetProperty("game", out var np3) && !string.IsNullOrWhiteSpace(np3.GetString())) item.Name = np3.GetString()!;
        else if (elem.TryGetProperty("game_name", out var np4) && !string.IsNullOrWhiteSpace(np4.GetString())) item.Name = np4.GetString()!;
        else if (elem.TryGetProperty("file", out var np5) && !string.IsNullOrWhiteSpace(np5.GetString()))
        {
            item.Name = np5.GetString()!.Replace(".zip", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ');
        }

        if (elem.TryGetProperty("header_image", out var ip)) item.HeaderImage = ip.GetString();
        else if (elem.TryGetProperty("headerImage", out var ip2)) item.HeaderImage = ip2.GetString();
        else if (elem.TryGetProperty("capsuleImage", out var ip3)) item.HeaderImage = ip3.GetString();
        else if (elem.TryGetProperty("image", out var ip4)) item.HeaderImage = ip4.GetString();

        // Parse the inline fixes array (DepotBox returns fixes[] inside each game element)
        if (elem.TryGetProperty("fixes", out var fixesProp) && fixesProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var fixElem in fixesProp.EnumerateArray())
                item.Fixes.Add(ParseGameFixElement(fixElem));
        }

        // Fix count: explicit property → inline fixes array length → 0
        if (elem.TryGetProperty("fixCount", out var fc) && fc.TryGetInt32(out int f1)) item.FixCount = f1;
        else if (elem.TryGetProperty("fix_count", out var fc2) && fc2.TryGetInt32(out int f2)) item.FixCount = f2;
        else if (elem.TryGetProperty("fixes_count", out var fc3) && fc3.TryGetInt32(out int f3)) item.FixCount = f3;
        else if (elem.TryGetProperty("count", out var fc4) && fc4.TryGetInt32(out int f4)) item.FixCount = f4;
        else item.FixCount = item.Fixes.Count; // derive from parsed fixes

        // Tags: game-level first, then aggregate from parsed fixes if empty
        var tags = new List<GameFixTag>();
        if (elem.TryGetProperty("tags", out var tp))
        {
            if (tp.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tp.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.Object)
                    {
                        string id = t.TryGetProperty("id", out var tid) ? tid.GetString() ?? "" : "";
                        string n = t.TryGetProperty("name", out var tn) ? tn.GetString() ?? id : id;
                        if (!string.IsNullOrEmpty(id)) tags.Add(new GameFixTag { Id = id, Name = n, Slug = id });
                    }
                    else if (t.ValueKind == JsonValueKind.String)
                    {
                        string val = t.GetString() ?? "";
                        if (!string.IsNullOrEmpty(val)) tags.Add(new GameFixTag { Id = val.ToLowerInvariant(), Name = val, Slug = val.ToLowerInvariant() });
                    }
                }
            }
            else if (tp.ValueKind == JsonValueKind.String)
            {
                string val = tp.GetString() ?? "";
                foreach (var s in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    tags.Add(new GameFixTag { Id = s.ToLowerInvariant(), Name = s, Slug = s.ToLowerInvariant() });
            }
        }
        else if (elem.TryGetProperty("tag", out var tpSingle))
        {
            string val = tpSingle.ToString();
            foreach (var s in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                tags.Add(new GameFixTag { Id = s.ToLowerInvariant(), Name = s, Slug = s.ToLowerInvariant() });
        }

        // If no game-level tags found, aggregate from the parsed fixes
        if (tags.Count == 0 && item.Fixes.Count > 0)
        {
            tags = item.Fixes
                .SelectMany(f => f.Tags)
                .GroupBy(t => t.Id.ToLowerInvariant())
                .Select(g => g.First())
                .ToList();
        }

        // Last resort: infer from the name
        if (tags.Count == 0)
        {
            string combo = (item.Name + " " + item.AppId).ToLowerInvariant();
            if (combo.Contains("online")) tags.Add(new GameFixTag { Id = "online", Name = "Online", Slug = "online" });
            else if (combo.Contains("hypervisor") || combo.Contains("denuvo")) tags.Add(new GameFixTag { Id = "hypervisor", Name = "Hypervisor", Slug = "hypervisor" });
            else tags.Add(new GameFixTag { Id = "bypass", Name = "Bypass", Slug = "bypass" });
        }

        item.Tags = tags;
        return item;
    }

    /// <summary>Fetches a game's fixes from DepotBox /api/game-fixes?q=appid.</summary>
    public async Task<GameFixesResponse?> GetGameFixesAsync(string appid, CancellationToken ct = default)
    {
        string? name = _appList.GetName(long.TryParse(appid, out long aid) ? aid : 0);
        string? headerImage = null;
        List<GameFix> allFixes = [];

        var endpoints = new[]
        {
            $"{AppConfig.DepotBoxProxyUrl}/api/game-fixes?q={Uri.EscapeDataString(appid)}",
            $"https://depotbox.org/api/game-fixes?q={Uri.EscapeDataString(appid)}",
            $"https://depotbox.pages.dev/api/game-fixes?q={Uri.EscapeDataString(appid)}"
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
                    var data = ParseGameFixes(json, appid);
                    if (data is not null && data.Fixes.Count > 0)
                    {
                        if (!string.IsNullOrEmpty(data.Name) && data.Name != appid) name ??= data.Name;
                        if (!string.IsNullOrEmpty(data.HeaderImage)) headerImage ??= data.HeaderImage;
                        allFixes.AddRange(data.Fixes);
                        break;
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

        return new GameFixesResponse
        {
            AppId = appid,
            Name = name ?? appid,
            HeaderImage = headerImage ?? (long.TryParse(appid, out long parsedId) ? SteamAppInfoCache.GuessHeaderImageUrl(parsedId) : null),
            Fixes = mergedFixes
        };
    }

    public static GameFixesResponse CreateFallbackFixesResponse(string appid, string? gameName)
    {
        long.TryParse(appid, out long aid);
        string name = gameName ?? (aid > 0 ? $"App {aid}" : appid);
        string headerImage = aid > 0 ? SteamAppInfoCache.GuessHeaderImageUrl(aid) : "";
        string cleanName = name.Replace(' ', '_');

        return new GameFixesResponse
        {
            AppId = appid,
            Name = name,
            HeaderImage = headerImage,
            Fixes = new List<GameFix>
            {
                new GameFix
                {
                    Id = $"{appid}_bypass",
                    Title = $"{name} Bypass Fix",
                    Description = "Bypass game fix release for this title.",
                    HasManifest = true,
                    HasFix = true,
                    ManifestFilename = $"{appid}.zip",
                    FixFilename = $"{cleanName}_bypass.zip",
                    Tags = new List<GameFixTag>
                    {
                        new GameFixTag { Id = "bypass", Name = "Bypass", Slug = "bypass" }
                    }
                }
            }
        };
    }

    internal static GameFixesResponse? ParseGameFixes(string json, string appid)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var fixes = new List<GameFix>();
            string? name = null;
            string? img = null;

            if (root.ValueKind == JsonValueKind.Object)
            {
                // DepotBox ?q= returns { success, games: [{ appid, name, fixes: [...] }] }
                if (root.TryGetProperty("games", out var gamesProp) && gamesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var gameElem in gamesProp.EnumerateArray())
                    {
                        // Match on appid (the ?q= search may return multiple games)
                        string? elemAppId = gameElem.TryGetProperty("appid", out var eid) ? eid.ToString() : null;
                        if (elemAppId is not null && elemAppId != appid) continue;

                        if (name is null && gameElem.TryGetProperty("name", out var gn)) name = gn.GetString();
                        if (img is null)
                        {
                            if (gameElem.TryGetProperty("headerImage", out var ghi)) img = ghi.GetString();
                            else if (gameElem.TryGetProperty("header_image", out var ghi2)) img = ghi2.GetString();
                            else if (gameElem.TryGetProperty("capsuleImage", out var gci)) img = gci.GetString();
                        }

                        if (gameElem.TryGetProperty("fixes", out var fixesProp) && fixesProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var elem in fixesProp.EnumerateArray())
                                fixes.Add(ParseGameFixElement(elem));
                        }

                        if (elemAppId == appid) break; // exact match found
                    }
                }
                // Fallback: direct { name, fixes: [...] } format
                else if (root.TryGetProperty("fixes", out var fixesProp) && fixesProp.ValueKind == JsonValueKind.Array)
                {
                    if (root.TryGetProperty("name", out var n)) name = n.GetString();
                    if (root.TryGetProperty("header_image", out var hi)) img = hi.GetString();
                    else if (root.TryGetProperty("headerImage", out var hi2)) img = hi2.GetString();

                    foreach (var elem in fixesProp.EnumerateArray())
                        fixes.Add(ParseGameFixElement(elem));
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in root.EnumerateArray())
                    fixes.Add(ParseGameFixElement(elem));
            }

            if (fixes.Count > 0)
            {
                return new GameFixesResponse { AppId = appid, Name = name ?? appid, HeaderImage = img, Fixes = fixes };
            }
        }
        catch { }
        return null;
    }

    private static GameFix ParseGameFixElement(JsonElement elem)
    {
        var fix = new GameFix();
        if (elem.TryGetProperty("id", out var id)) fix.Id = id.GetString() ?? "";
        if (elem.TryGetProperty("title", out var t)) fix.Title = t.GetString() ?? fix.Id;
        else if (elem.TryGetProperty("name", out var n)) fix.Title = n.GetString() ?? fix.Id;

        // DepotBox uses downloadName / filename for the file to download
        string? downloadName = null;
        if (elem.TryGetProperty("downloadName", out var dn)) downloadName = dn.GetString();
        else if (elem.TryGetProperty("filename", out var fn)) downloadName = fn.GetString();

        if (string.IsNullOrEmpty(fix.Title) && !string.IsNullOrEmpty(downloadName))
            fix.Title = downloadName.Replace(".zip", "").Replace(".rar", "").Replace('_', ' ');
        if (string.IsNullOrEmpty(fix.Title)) fix.Title = fix.Id;

        if (elem.TryGetProperty("description", out var d)) fix.Description = d.GetString();
        // Include size in description if available
        if (elem.TryGetProperty("size", out var sz) && !string.IsNullOrEmpty(sz.GetString()))
            fix.Description = string.IsNullOrEmpty(fix.Description) ? $"Size: {sz.GetString()}" : $"{fix.Description} (Size: {sz.GetString()})";

        fix.HasManifest = !elem.TryGetProperty("hasManifest", out var hm) || hm.GetBoolean();
        fix.HasFix = !elem.TryGetProperty("hasFix", out var hf) || hf.GetBoolean();

        if (elem.TryGetProperty("manifestFilename", out var mf)) fix.ManifestFilename = mf.GetString();
        if (elem.TryGetProperty("fixFilename", out var ff)) fix.FixFilename = ff.GetString();
        else if (elem.TryGetProperty("file", out var ffile)) fix.FixFilename = ffile.GetString();
        // DepotBox: use downloadName as the fix filename if not already set
        if (string.IsNullOrEmpty(fix.FixFilename) && !string.IsNullOrEmpty(downloadName))
            fix.FixFilename = downloadName;

        var tags = new List<GameFixTag>();
        if (elem.TryGetProperty("tags", out var tp))
        {
            if (tp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tp.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        string tid = item.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                        string tn = item.TryGetProperty("name", out var name) ? name.GetString() ?? tid : tid;
                        if (!string.IsNullOrEmpty(tid)) tags.Add(new GameFixTag { Id = tid, Name = tn, Slug = tid });
                    }
                    else if (item.ValueKind == JsonValueKind.String)
                    {
                        string val = item.GetString() ?? "";
                        if (!string.IsNullOrEmpty(val)) tags.Add(new GameFixTag { Id = val.ToLowerInvariant(), Name = val, Slug = val.ToLowerInvariant() });
                    }
                }
            }
        }
        else if (elem.TryGetProperty("tag", out var tpSingle))
        {
            string val = tpSingle.ToString();
            foreach (var s in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                tags.Add(new GameFixTag { Id = s.ToLowerInvariant(), Name = s, Slug = s.ToLowerInvariant() });
        }

        if (tags.Count == 0)
        {
            string combo = (fix.Title + " " + fix.Id).ToLowerInvariant();
            if (combo.Contains("online")) tags.Add(new GameFixTag { Id = "online", Name = "Online", Slug = "online" });
            else if (combo.Contains("hypervisor") || combo.Contains("denuvo")) tags.Add(new GameFixTag { Id = "hypervisor", Name = "Hypervisor", Slug = "hypervisor" });
            else tags.Add(new GameFixTag { Id = "bypass", Name = "Bypass", Slug = "bypass" });
        }

        fix.Tags = tags;
        return fix;
    }

    /// <summary>Downloads a fix archive from DepotBox /api/game-fixes/download or a manifest zip.</summary>
    public async Task<DownloadedFile> DownloadGameFixAsync(
        string fixId, string slot, string fallbackName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (slot == "manifest")
        {
            string manifestUrl = $"{AppConfig.ApiBaseUrl}/api/manifest/download?appid={Uri.EscapeDataString(fixId)}";
            return await DownloadFileAsync(manifestUrl, $"{fixId}.zip", progress, ct);
        }

        string downloadUrl = $"{AppConfig.DepotBoxProxyUrl}/api/game-fixes/download?id={Uri.EscapeDataString(fixId)}&file={Uri.EscapeDataString(fallbackName)}";
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.IsSuccessStatusCode)
            {
                return await HttpFileDownloader.SaveResponseAsync(res, fallbackName, progress, ct);
            }
        }
        catch { }

        // Upstream fallback
        string fallbackUrl = $"https://depotbox.org/api/game-fixes/download?id={Uri.EscapeDataString(fixId)}&file={Uri.EscapeDataString(fallbackName)}";
        return await DownloadFromUrlAsync(fallbackUrl, fallbackName, progress, ct);
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
