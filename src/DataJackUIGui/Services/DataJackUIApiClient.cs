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

    /// <summary>DepotBox game fixes endpoint. Returns game listings and the 3 DepotBox tags (Bypass, Online, Hypervisor).</summary>
    public async Task<DenuvoListingsResponse?> GetDenuvoListingsAsync(CancellationToken ct = default)
    {
        try { await _appList.EnsureLoadedAsync(); } catch { }

        List<DenuvoGameListing> allGames = [];
        var tags = new List<DenuvoTag>
        {
            new DenuvoTag { Id = "bypass", Name = "Bypass", Slug = "bypass" },
            new DenuvoTag { Id = "online", Name = "Online", Slug = "online" },
            new DenuvoTag { Id = "hypervisor", Name = "Hypervisor", Slug = "hypervisor" }
        };

        var endpoints = new[]
        {
            $"{AppConfig.ApiBaseUrl}/api/depotbox/api/game-fixes?tag=bypass,online,hypervisor",
            "https://depotbox.org/api/game-fixes?tag=bypass,online,hypervisor"
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
                    }
                }
            }
            catch { }
        }

        var mergedGames = allGames.DistinctBy(g => g.AppId).ToList();
        return new DenuvoListingsResponse { Games = mergedGames, Tags = tags };
    }

    private static DenuvoListingsResponse? ParseDenuvoListings(string json, SteamAppListCache appList)
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

                if (long.TryParse(appid, out long aid))
                {
                    string? cacheName = appList.GetName(aid);
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
                                if (!string.IsNullOrWhiteSpace(tStr))
                                    itemTags.Add(new DenuvoTag { Id = tStr.ToLower(), Name = tStr, Slug = tStr.ToLower() });
                            }
                            else if (tagElem.ValueKind == JsonValueKind.Object)
                            {
                                var tagObj = JsonSerializer.Deserialize<DenuvoTag>(tagElem.GetRawText(), JsonOpts);
                                if (tagObj is not null) itemTags.Add(tagObj);
                            }
                        }
                    }
                    else if (tagsProp.ValueKind == JsonValueKind.String)
                    {
                        string tStr = tagsProp.GetString() ?? "";
                        foreach (var part in tStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            itemTags.Add(new DenuvoTag { Id = part.ToLower(), Name = part, Slug = part.ToLower() });
                        }
                    }
                }

                if (item.TryGetProperty("tag", out var singleTag) && singleTag.ValueKind == JsonValueKind.String)
                {
                    string tStr = singleTag.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(tStr) && !itemTags.Any(t => string.Equals(t.Id, tStr, StringComparison.OrdinalIgnoreCase)))
                    {
                        itemTags.Add(new DenuvoTag { Id = tStr.ToLower(), Name = tStr, Slug = tStr.ToLower() });
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
                var defaultTags = new List<DenuvoTag>
                {
                    new DenuvoTag { Id = "bypass", Name = "Bypass", Slug = "bypass" },
                    new DenuvoTag { Id = "online", Name = "Online", Slug = "online" },
                    new DenuvoTag { Id = "hypervisor", Name = "Hypervisor", Slug = "hypervisor" }
                };
                return new DenuvoListingsResponse { Games = dict.Values.ToList(), Tags = defaultTags };
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

    /// <summary>Downloads a fix archive from DepotBox /api/game-fixes/download or a manifest zip.</summary>
    public async Task<DownloadedFile> DownloadDenuvoAsync(
        string fixId, string slot, string fallbackName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (slot == "manifest")
        {
            string manifestUrl = $"{AppConfig.ApiBaseUrl}/api/manifest/download?appid={Uri.EscapeDataString(fixId)}";
            return await DownloadFileAsync(manifestUrl, $"{fixId}.zip", progress, ct);
        }

        string proxyUrl = $"{AppConfig.ApiBaseUrl}/api/depotbox/api/game-fixes/download?id={Uri.EscapeDataString(fixId)}&file={Uri.EscapeDataString(fallbackName)}";
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, proxyUrl);
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
