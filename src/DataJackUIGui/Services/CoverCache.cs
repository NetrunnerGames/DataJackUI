using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace DataJackUIGui.Services;

/// <summary>
/// Caches Steam cover images on disk so each is downloaded once, then loaded locally/offline.
/// Dedups concurrent downloads and remembers appids with no cover (so they stop retrying).
/// </summary>
public class CoverCache
{
    private static readonly string CoversDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUIGui", "covers");

    // Reject only truncated/empty bodies. We don't gate on a size threshold: some legit covers are
    // tiny (e.g. Undertale 391540's header.jpg is ~5 KB. Mostly black, highly compressible), and a
    // size gate wrongly rejected them. Validity is decided by JPEG magic bytes instead (see IsJpeg).
    private const int MinValidBytes = 512;

    // Steam's grey "Header Capsule" placeholder. The predictable CDN URL
    // (cdn.../steam/apps/<id>/header.jpg) serves this static image for apps that have no CDN header yet
    // (typically newer releases, e.g. Silent Hill f 2947440). It's a *valid* JPEG, so the magic-byte
    // check passes and it would otherwise be cached as the cover. We fingerprint it exactly (byte length
    // + SHA-256) and reject it so the caller falls back to the appdetails header_image (the real art).
    private const int PlaceholderLength = 9816;
    private const string PlaceholderSha256 =
        "732ec27f2af650fe079f1c83b0bb0c712a322dc175f383504176724675ad2700";

    /// <summary>True if these bytes are Steam's "Header Capsule" placeholder (not a real cover).</summary>
    private static bool IsHeaderCapsulePlaceholder(byte[] b)
    {
        if (b.Length != PlaceholderLength) return false;
        var hash = Convert.ToHexString(SHA256.HashData(b));
        return hash.Equals(PlaceholderSha256, StringComparison.OrdinalIgnoreCase);
    }

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly ConcurrentDictionary<long, byte> _noCover = new();
    private readonly ConcurrentDictionary<long, Task<string?>> _inFlight = new();
    private readonly SettingsService? _settings;

    public CoverCache(SettingsService? settings = null)
    {
        _settings = settings;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslErrors) => true
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private static string PathFor(long appid) => Path.Combine(CoversDir, $"{appid}.jpg");

    /// <summary>True if the bytes start with the JPEG magic number (FF D8 FF).</summary>
    private static bool IsJpeg(byte[] b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// <summary>Local cover path if already cached, else null. A cover cached as the grey "Header
    /// Capsule" placeholder by an older build is purged here so it re-resolves to the real art (cheap:
    /// only files whose length exactly matches the placeholder get hashed).</summary>
    public string? GetLocalPath(long appid)
    {
        string p = PathFor(appid);
        if (!File.Exists(p)) return null;

        try
        {
            if (new FileInfo(p).Length == PlaceholderLength &&
                IsHeaderCapsulePlaceholder(File.ReadAllBytes(p)))
            {
                File.Delete(p);
                return null; // stale placeholder removed → caller re-resolves via header_image
            }
        }
        catch { /* if we can't check/delete, just use what's there */ }

        return p;
    }

    /// <summary>True if we already determined this appid has no usable cover (don't keep retrying).</summary>
    public bool IsKnownMissing(long appid) => _noCover.ContainsKey(appid);

    /// <summary>True if the bytes start with JPEG, PNG, or WEBP magic numbers.</summary>
    private static bool IsValidImage(byte[] b) =>
        b.Length >= 8 && (
            IsJpeg(b) ||
            (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||
            (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46)
        );

    /// <summary>Remember that an appid has no cover from any source, so we stop attempting it.</summary>
    public void MarkMissing(long appid) => _noCover[appid] = 0;

    /// <summary>
    /// Return the cached cover path, downloading from <paramref name="remoteUrl"/> first if needed.
    /// Falls back to SteamGridDB (via Cloudflare worker proxy) if Steam CDN is unavailable.
    /// Rejects too-small responses (placeholder / error pages). Concurrent calls share one download.
    /// </summary>
    public Task<string?> EnsureAsync(long appid, string? remoteUrl, CancellationToken ct = default)
    {
        string path = PathFor(appid);
        if (File.Exists(path)) return Task.FromResult<string?>(path);
        if (IsKnownMissing(appid)) return Task.FromResult<string?>(null);

        // One download per appid even if asked concurrently (prefetch + page view).
        return _inFlight.GetOrAdd(appid, _ => DownloadAsync(appid, path, remoteUrl, ct));
    }

    private async Task<string?> DownloadAsync(long appid, string path, string? remoteUrl, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(remoteUrl))
            {
                string sanitizedUrl = SteamCdnUrl.Sanitize(remoteUrl) ?? remoteUrl;
                try
                {
                    byte[] bytes = await _http.GetByteArrayAsync(sanitizedUrl, ct);
                    if (bytes.Length >= MinValidBytes && IsValidImage(bytes) && !IsHeaderCapsulePlaceholder(bytes))
                    {
                        await _ioGate.WaitAsync(ct);
                        try
                        {
                            Directory.CreateDirectory(CoversDir);
                            await File.WriteAllBytesAsync(path, bytes, ct);
                            return path;
                        }
                        finally { _ioGate.Release(); }
                    }
                }
                catch { /* Steam CDN failed, try SteamGridDB fallback */ }
            }

            // Fallback to SteamGridDB via Cloudflare worker proxy
            string? fallbackUrl = await ResolveFallbackCoverUrlAsync(appid, ct);
            if (!string.IsNullOrWhiteSpace(fallbackUrl))
            {
                try
                {
                    byte[] bytes = await _http.GetByteArrayAsync(fallbackUrl, ct);
                    if (bytes.Length >= MinValidBytes && IsValidImage(bytes))
                    {
                        await _ioGate.WaitAsync(ct);
                        try
                        {
                            Directory.CreateDirectory(CoversDir);
                            await File.WriteAllBytesAsync(path, bytes, ct);
                            return path;
                        }
                        finally { _ioGate.Release(); }
                    }
                }
                catch { /* Fallback download failed */ }
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _inFlight.TryRemove(appid, out _);
        }
    }

    private async Task<string?> ResolveFallbackCoverUrlAsync(long appid, CancellationToken ct)
    {
        try
        {
            string url = $"{AppConfig.ApiBaseUrl}/api/game-fallback?appid={appid}";
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;

            using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("header_image", out var imgProp) &&
                imgProp.GetString() is { Length: > 0 } imgUrl)
            {
                return imgUrl;
            }
        }
        catch { /* best effort */ }

        return null;
    }
}
