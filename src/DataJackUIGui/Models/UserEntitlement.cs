using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataJackUIGui.Models;

/// <summary>
/// Handles deserialization of allowed_appids, which can contain numbers (e.g. [730, 2567870])
/// or wildcards (e.g. ["*"] for software owner/lifetime admin).
/// </summary>
public class AllowedAppIdsConverter : JsonConverter<List<long>>
{
    public override List<long> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new List<long>();
        if (reader.TokenType != JsonTokenType.StartArray)
            return list;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long num))
            {
                list.Add(num);
            }
            else if (reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), out long parsedNum))
            {
                list.Add(parsedNum);
            }
            // Strings like "*" are silently handled (wildcard is covered by IsOwner / IsLifetimePremium)
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<long> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var id in value)
        {
            writer.WriteNumberValue(id);
        }
        writer.WriteEndArray();
    }
}

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
    [JsonPropertyName("allowed_appids")]
    [JsonConverter(typeof(AllowedAppIdsConverter))]
    public List<long> AllowedAppIds { get; init; } = new();
    [JsonPropertyName("guild_ids")] public List<string> GuildIds { get; init; } = new();

    public bool EffectiveIsOwner => IsOwner || (!string.IsNullOrEmpty(DiscordId) && DiscordId == AppConfig.OwnerDiscordId);

    public bool IsLifetimePremium => EffectiveIsOwner || Tier.Equals("premium", StringComparison.OrdinalIgnoreCase) || Tier.Equals("owner", StringComparison.OrdinalIgnoreCase);
    public bool IsSingleTitle => !IsLifetimePremium && Tier.Equals("single", StringComparison.OrdinalIgnoreCase);

    public bool IsInServer(string serverId) =>
        EffectiveIsOwner || string.IsNullOrEmpty(serverId) || GuildIds.Contains(serverId);

    public bool IsAppAllowed(long appid)
    {
        if (IsBanned) return false;
        if (IsLifetimePremium) return true;
        return AllowedAppIds.Contains(appid);
    }
}
