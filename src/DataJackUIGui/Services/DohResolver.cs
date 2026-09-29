using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DataJackUIGui.Services;

/// <summary>
/// Cloudflare DNS over HTTPS (DoH) resolver for fallback/enforced resolution
/// when system DNS is blocked or throttled (e.g. by ISPs).
/// </summary>
public class DohResolver
{
    public const string Endpoint = "https://1.1.1.1/dns-query";

    private static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(30.0);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(5.0);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (IPAddress[] Addresses, DateTimeOffset Expires)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public DohResolver(HttpMessageHandler? handler = null)
    {
        _http = handler == null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(5.0) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5.0) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
    }

    public static bool ShouldBypass(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        if (IPAddress.TryParse(host, out _)) return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return !host.Contains('.');
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (ShouldBypass(host)) return [];
        if (_cache.TryGetValue(host, out var entry) && entry.Expires > DateTimeOffset.UtcNow)
            return entry.Addresses;

        try
        {
            string url = $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(host)}&type=A";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return [];

            var (addrs, ttl) = Parse(await resp.Content.ReadAsStringAsync(ct));
            if (addrs.Length == 0) return [];

            TimeSpan effectiveTtl = ttl < MinTtl ? MinTtl : (ttl > MaxTtl ? MaxTtl : ttl);
            _cache[host] = (addrs, DateTimeOffset.UtcNow + effectiveTtl);
            return addrs;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return []; }
    }

    public static (IPAddress[] Addresses, TimeSpan Ttl) Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ([], default);
            if (!root.TryGetProperty("Status", out var status) || status.GetInt32() != 0) return ([], default);
            if (!root.TryGetProperty("Answer", out var answer) || answer.ValueKind != JsonValueKind.Array) return ([], default);

            var list = new List<IPAddress>();
            int minTtl = int.MaxValue;
            foreach (var item in answer.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) && (type.GetInt32() == 1 || type.GetInt32() == 28))
                {
                    if (item.TryGetProperty("data", out var dataStr) && IPAddress.TryParse(dataStr.GetString(), out var addr))
                    {
                        list.Add(addr);
                        if (item.TryGetProperty("TTL", out var ttlVal) && ttlVal.TryGetInt32(out int t) && t < minTtl)
                            minTtl = t;
                    }
                }
            }

            if (list.Count == 0) return ([], default);
            TimeSpan ttl = minTtl != int.MaxValue ? TimeSpan.FromSeconds(minTtl) : MinTtl;
            return (list.ToArray(), ttl);
        }
        catch { return ([], default); }
    }
}
