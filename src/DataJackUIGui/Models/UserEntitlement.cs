using System.Text.Json.Serialization;

namespace DataJackUIGui.Models;

/// <summary>
/// User entitlement & access control profile from server API / Supabase KV.
/// Entitlements are 100% server-enforced: the server validates Discord ID/OAuth session,
/// checks user_entitlements in Supabase, and returns allowed tiers and app permissions.
/// </summary>
public record UserEntitlement
{
    [JsonPropertyName("discord_id")] public string DiscordId { get; init; } = "";
    [JsonPropertyName("ip_address")] public string IpAddress { get; init; } = "";
    [JsonPropertyName("is_banned")] public bool IsBanned { get; init; } = false;
    [JsonPropertyName("is_owner")] public bool IsOwner { get; init; } = false;
    [JsonPropertyName("tier")] public string Tier { get; init; } = "single"; // "premium" (lifetime), "single" (per-game)
    [JsonPropertyName("allowed_appids")] public List<long> AllowedAppIds { get; init; } = new();
    [JsonPropertyName("guild_ids")] public List<string> GuildIds { get; init; } = new();

    public bool IsLifetimePremium => IsOwner || Tier.Equals("premium", StringComparison.OrdinalIgnoreCase) || Tier.Equals("owner", StringComparison.OrdinalIgnoreCase);
    public bool IsSingleTitle => !IsLifetimePremium && Tier.Equals("single", StringComparison.OrdinalIgnoreCase);

    public bool IsInServer(string serverId) =>
        IsOwner || string.IsNullOrEmpty(serverId) || GuildIds.Contains(serverId);

    public bool IsAppAllowed(long appid)
    {
        if (IsBanned) return false;
        if (IsLifetimePremium) return true;
        return AllowedAppIds.Contains(appid);
    }
}
