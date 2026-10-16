using System.Text.Json.Serialization;

namespace DataJackUIGui.Models;

// ── lua.tools API DTOs ──────────────────────────────────────────────

public class SteamSearchResult
{
    public long AppId { get; set; }
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
}

// Steam's public store-search response (called directly by the app)
public class SteamStoreSearchResponse
{
    [JsonPropertyName("items")] public List<SteamStoreItem> Items { get; set; } = [];
}

public class SteamStoreItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tiny_image")] public string? TinyImage { get; set; }
}

// ── Steam featuredcategories (Add page "Featured" strips) ───────────
// Only the two app-list categories we surface are modeled. Each item carries everything we render
// (appid + name + wide capsule art), so no per-app lookup is needed.
public class SteamFeaturedResponse
{
    [JsonPropertyName("top_sellers")] public SteamFeaturedCategory? TopSellers { get; set; }
    [JsonPropertyName("new_releases")] public SteamFeaturedCategory? NewReleases { get; set; }
}

public class SteamFeaturedCategory
{
    [JsonPropertyName("items")] public List<SteamFeaturedItem> Items { get; set; } = [];
}

public class SteamFeaturedItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    // 616×353 capsule. The nice wide art for a featured card.
    [JsonPropertyName("large_capsule_image")] public string? LargeCapsuleImage { get; set; }
    [JsonPropertyName("type")] public int Type { get; set; } // 0 = game; non-zero are bundles/etc., so skip
}

public class GameDetails
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("appid")] public long AppId { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("baseAppId")] public string? BaseAppId { get; set; }
    [JsonPropertyName("genres")] public List<string> Genres { get; set; } = [];
    [JsonPropertyName("headerImage")] public string? HeaderImage { get; set; }
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("drmNotice")] public string? DrmNotice { get; set; }

    [JsonIgnore] public bool IsDlc => string.Equals(Type, "dlc", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool HasDenuvo => !string.IsNullOrEmpty(DrmNotice) && DrmNotice.Contains("Denuvo", StringComparison.OrdinalIgnoreCase);
}

public class DlcDepot
{
    [JsonPropertyName("depotId")] public string DepotId { get; set; } = "";
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("oslist")] public string? OsList { get; set; }
    [JsonPropertyName("included")] public bool Included { get; set; }

    [JsonIgnore]
    public string Meta
    {
        get
        {
            var parts = new List<string> { Language ?? "default" };
            if (!string.IsNullOrEmpty(OsList)) parts.Add(OsList);
            return string.Join(" · ", parts);
        }
    }
}

public class DlcInfo
{
    [JsonPropertyName("appid")] public string AppId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("depotCount")] public int DepotCount { get; set; }
    [JsonPropertyName("haveCount")] public int HaveCount { get; set; }
    [JsonPropertyName("missingCount")] public int MissingCount { get; set; }
    [JsonPropertyName("depots")] public List<DlcDepot> Depots { get; set; } = [];
}

/// <summary>Hubcap (hubcapmanifest.com) <c>/api/v1/user/stats</c> response. Usage for the user's own key.</summary>
public class HubcapStats
{
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("daily_usage")] public int DailyUsage { get; set; }
    [JsonPropertyName("daily_limit")] public int DailyLimit { get; set; }
    [JsonPropertyName("can_make_requests")] public bool CanMakeRequests { get; set; }
    [JsonPropertyName("api_key_expires_at")] public string? ApiKeyExpiresAt { get; set; }
}

/// <summary>Hubcap <c>/api/v1/generate/usage</c> response for single manifest quota.</summary>
public class HubcapSingleUsage
{
    [JsonPropertyName("usage")] public int Usage { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("remaining")] public int Remaining { get; set; }
}

public class HubcapGenerationUsage
{
    [JsonPropertyName("single")] public HubcapSingleUsage? Single { get; set; }
    [JsonPropertyName("steam_service_ready")] public bool SteamServiceReady { get; set; } = true;
}

/// <summary>Hubcap <c>/api/v1/status/{appid}</c> response. Whether a manifest exists (free, no usage count).</summary>
public class HubcapManifestStatus
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("manifest_file_exists")] public bool ManifestFileExists { get; set; }
}

public class ApiError
{
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>The lua.tools standard daily download usage (counted from user_downloads, limit 25/day).</summary>
public record StandardUsage(int Used, int Limit);

public class SupporterStatus
{
    [JsonPropertyName("isSupporter")] public bool IsSupporter { get; set; }
}

// ── Supabase auth DTOs ──────────────────────────────────────────────

public class SupabaseSession
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("user")] public SupabaseUser? User { get; set; }
}

public class SupabaseUser
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("user_metadata")] public UserMetadata? Metadata { get; set; }
}

public class UserMetadata
{
    [JsonPropertyName("full_name")] public string? FullName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("user_name")] public string? UserName { get; set; }
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    [JsonPropertyName("custom_claims")] public CustomClaims? CustomClaims { get; set; }
}

public class CustomClaims
{
    [JsonPropertyName("global_name")] public string? GlobalName { get; set; }
    [JsonPropertyName("preferred_username")] public string? PreferredUsername { get; set; }
}

/// <summary>Persisted machine-bound encrypted auth session state.</summary>
public class StoredAuth
{
    public string RefreshToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string? Username { get; set; }
    public string? UserId { get; set; }
    public string? AvatarUrl { get; set; }
}

/// <summary>Per-source UI metadata, mirroring src/lib/source-meta.ts on the website.</summary>
public static class SourceMeta
{
    public record Meta(string? DisplayName = null, string? DiscordUrl = null, bool RequiresUserKey = false);

    public static readonly Dictionary<string, Meta> All = new()
    {
        ["DepotBox"] = new(DisplayName: "DepotBox", DiscordUrl: "https://depotbox.org"),
        ["Ryuu"] = new(DiscordUrl: "https://discord.gg/manifests"),
        ["TwentyTwo Cloud"] = new(DiscordUrl: "https://discord.gg/RrukXPyv5b"),
        ["Sushi"] = new(DiscordUrl: "https://discord.gg/hMdv5dQhcN"),
        ["Skyflare"] = new(DiscordUrl: "https://github.com/skyflarefox"),
        ["Sadie (Morrenus)"] = new(DisplayName: "Sadie (Hubcap)", DiscordUrl: "https://discord.gg/hubcapsmanifest", RequiresUserKey: true),
    };

    public static int GetPriority(string name)
    {
        // 1st Priority: DepotBox
        if (string.Equals(name, "DepotBox", StringComparison.OrdinalIgnoreCase)) return 100;

        // 2nd Priority: Ryuu / Ryuu Direct
        if (string.Equals(name, "Ryuu", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Ryuu", StringComparison.OrdinalIgnoreCase)) return 90;

        var meta = Get(name);
        if (meta.RequiresUserKey) return 10;

        // 3rd Priority: Public / community sources (Sushi, TwentyTwo Cloud, Skyflare, etc.)
        return 50;
    }

    public static Meta Get(string name) => All.TryGetValue(name, out var m) ? m : new Meta();
}

public class RyuuManifestInfo
{
    [JsonPropertyName("app_id")] public string? AppId { get; set; }
    [JsonPropertyName("available_branches")] public List<string>? AvailableBranches { get; set; }
    [JsonPropertyName("default_branch")] public string? DefaultBranch { get; set; }
    [JsonPropertyName("branch_manifests")] public Dictionary<string, RyuuBranchManifest>? BranchManifests { get; set; }
    [JsonPropertyName("depot_dlc_map")] public Dictionary<string, string>? DepotDlcMap { get; set; }
    [JsonPropertyName("depot_oslist_map")] public Dictionary<string, string>? DepotOslistMap { get; set; }
    [JsonPropertyName("dlc")] public List<RyuuDlcInfo>? Dlc { get; set; }
    [JsonPropertyName("local_branches")] public List<string>? LocalBranches { get; set; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
    [JsonPropertyName("workshop_depot")] public int WorkshopDepot { get; set; }
    [JsonPropertyName("workshop_in_lua")] public bool WorkshopInLua { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }

    [JsonIgnore]
    public bool IsAvailable =>
        !string.Equals(Status, "warming", StringComparison.OrdinalIgnoreCase) &&
        BranchManifests != null &&
        BranchManifests.Count > 0;

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> LatestSteamDepots
    {
        get
        {
            if (BranchManifests != null &&
                BranchManifests.TryGetValue(DefaultBranch ?? "public", out var b) &&
                b.Depots != null)
            {
                return b.Depots;
            }
            return new Dictionary<string, string>();
        }
    }

    [JsonIgnore]
    public IReadOnlyList<string> DatabaseFiles
    {
        get
        {
            if (BranchManifests != null &&
                BranchManifests.TryGetValue(DefaultBranch ?? "public", out var b) &&
                b.Files != null)
            {
                return b.Files;
            }
            return Array.Empty<string>();
        }
    }
}

public class RyuuBranchManifest
{
    [JsonPropertyName("depots")] public Dictionary<string, string>? Depots { get; set; }
    [JsonPropertyName("files")] public List<string>? Files { get; set; }
}

public class RyuuDlcInfo
{
    [JsonPropertyName("appid")] public long AppId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("in_lua")] public bool InLua { get; set; }
    [JsonPropertyName("has_depots")] public bool HasDepots { get; set; }
    [JsonPropertyName("oslist")] public List<string>? OsList { get; set; }
}

public record GameUpdateCheckResult(
    bool HasUpdate,
    bool RyuuHasNewManifests,
    IReadOnlyDictionary<long, string> OutdatedDepots,
    IReadOnlyDictionary<string, string> LatestSteamDepots,
    IReadOnlyList<string> RyuuDatabaseFiles,
    IReadOnlyList<RyuuDlcInfo> DlcInLua);

