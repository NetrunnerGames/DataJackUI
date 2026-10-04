using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataJackUIGui.Models;
using DataJackUIGui.Services.Downloads;

namespace DataJackUIGui.Services;

/// <summary>
/// Talks to Hubcap (hubcapmanifest.com) DIRECTLY with the user's own API key.
/// Supports stats, status checks, zip manifest downloads, and single manifest generation.
/// Enforces a strict 5-second minimum interval between single manifest generation requests
/// to comply with Hubcap anti-scrape / rate limiting rules.
/// </summary>
public partial class HubcapService
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(AppConfig.HubcapBaseUrl),
        Timeout = TimeSpan.FromMinutes(5),
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly SemaphoreSlim SingleManifestRateLimiter = new(1, 1);
    private static DateTimeOffset _lastSingleManifestRequestTime = DateTimeOffset.MinValue;

    [GeneratedRegex("^smm_[0-9a-f]{96}$")]
    private static partial Regex KeyFormatRegex();

    /// <summary>Local format check. Hubcap keys are "smm_" followed by 96 lowercase hex chars.</summary>
    public static bool IsValidKeyFormat(string? key) => key is not null && KeyFormatRegex().IsMatch(key);

    /// <summary>Usage stats for a key. Null on any network/auth failure, never throws.</summary>
    public async Task<HubcapStats?> GetStatsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetAsync($"/api/v1/user/stats?api_key={Uri.EscapeDataString(key)}", ct);
            if (!res.IsSuccessStatusCode) return null;
            return await ReadJsonAsync<HubcapStats>(res, ct);
        }
        catch { return null; }
    }

    /// <summary>Whether a manifest exists for an app (free, no usage count). Null on failure.</summary>
    public async Task<HubcapManifestStatus?> CheckStatusAsync(string key, string appid, CancellationToken ct = default)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/status/{Uri.EscapeDataString(appid)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            return await ReadJsonAsync<HubcapManifestStatus>(res, ct);
        }
        catch { return null; }
    }

    /// <summary>Download the manifest zip for an app directly from Hubcap (counts toward the key's daily
    /// limit). Throws <see cref="ApiException"/> on failure so the download flow can report it.</summary>
    public async Task<DownloadedFile> DownloadManifestAsync(
        string appid, string key, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        var url = $"/api/v1/manifest/{Uri.EscapeDataString(appid)}?api_key={Uri.EscapeDataString(key)}";
        using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new ApiException($"Hubcap download failed with HTTP {(int)res.StatusCode}");
        }
        return await HttpFileDownloader.SaveResponseAsync(res, $"{appid}.zip", progress, ct);
    }

    /// <summary>
    /// Generate a single depot manifest file directly from Hubcap.
    /// GET /api/v1/generate/manifest?depot_id={depot_id}&manifest_id={manifest_id}
    /// Headers: Authorization: Bearer {key}
    /// Returns the binary .manifest file bytes.
    /// Enforces a strict minimum 5-second delay between requests to avoid anti-scrape detection and key bans.
    /// </summary>
    public async Task<byte[]> GenerateSingleManifestAsync(long depotId, long manifestId, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ApiException("Hubcap API key is not configured in Settings.");

        await SingleManifestRateLimiter.WaitAsync(ct);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastSingleManifestRequestTime;
            var requiredDelay = TimeSpan.FromSeconds(5) - elapsed;
            if (requiredDelay > TimeSpan.Zero)
            {
                await Task.Delay(requiredDelay, ct);
            }
            _lastSingleManifestRequestTime = DateTimeOffset.UtcNow;
        }
        finally
        {
            SingleManifestRateLimiter.Release();
        }

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/generate/manifest?depot_id={depotId}&manifest_id={manifestId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            string err = await res.Content.ReadAsStringAsync(ct);
            throw new ApiException($"Hubcap single manifest generation failed ({(int)res.StatusCode}): {(string.IsNullOrWhiteSpace(err) ? res.ReasonPhrase : err)}");
        }

        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Save generated single manifest bytes into Steam's depotcache folder ({depot_id}_{manifest_id}.manifest).
    /// </summary>
    public string SaveSingleManifestToDepotCache(long depotId, long manifestId, byte[] content, SteamService steam)
    {
        string? dir = steam.DepotCacheDir;
        if (string.IsNullOrWhiteSpace(dir))
            throw new InvalidOperationException("Steam depotcache directory could not be resolved.");

        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, $"{depotId}_{manifestId}.manifest");
        File.WriteAllBytes(filePath, content);
        return filePath;
    }

    // ── Plumbing ────────────────────────────────────────────────────

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
}
