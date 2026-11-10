namespace DataJackUIGui.Services;

/// <summary>
/// Utility to sanitize Steam asset CDN URLs. Replaces Akamai/Fastly hostnames with Cloudflare hostnames
/// to prevent image load failures caused by regional blockades or Akamai/Fastly server issues.
/// </summary>
public static class SteamCdnUrl
{
    public static string? Sanitize(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        return url
            .Replace("shared.akamai.steamstatic.com", "shared.cloudflare.steamstatic.com")
            .Replace("shared.fastly.steamstatic.com", "shared.cloudflare.steamstatic.com")
            .Replace("cdn.akamai.steamstatic.com", "cdn.cloudflare.steamstatic.com")
            .Replace("cdn.fastly.steamstatic.com", "cdn.cloudflare.steamstatic.com")
            .Replace("akamai.steamstatic.com", "cloudflare.steamstatic.com")
            .Replace("fastly.steamstatic.com", "cloudflare.steamstatic.com");
    }
}
