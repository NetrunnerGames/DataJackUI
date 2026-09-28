namespace DataJackUIGui.Models;

/// <summary>
/// User entitlement & access control profile from Supabase KV.
/// Key is Discord User ID (or IP address as alt identifier).
/// Controls banned status, entitlement tier (free, premium/lifetime, single), allowed appids, and download limits.
/// </summary>
public record UserEntitlement
{
    public string DiscordId { get; init; } = "";
    public string IpAddress { get; init; } = "";
    public bool IsBanned { get; init; } = false;
    public string Tier { get; init; } = "single"; // "premium" (lifetime), "single" (per-game $1/100 INR)
    public List<long> AllowedAppIds { get; init; } = new();
    public List<string> GuildIds { get; init; } = new();

    public bool IsLifetimePremium => Tier.Equals("premium", StringComparison.OrdinalIgnoreCase);
    public bool IsSingleTitle => Tier.Equals("single", StringComparison.OrdinalIgnoreCase);

    public bool IsInServer(string serverId) =>
        string.IsNullOrEmpty(serverId) || GuildIds.Contains(serverId);

    public bool IsAppAllowed(long appid)
    {
        if (IsBanned) return false;
        if (IsLifetimePremium) return true;
        return AllowedAppIds.Contains(appid);
    }
}
