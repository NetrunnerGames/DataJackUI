using System.IO;
using System.Text.Json;

namespace DataJackUIGui.Services;

public class AppSettings
{
    public string? SteamPathOverride { get; set; }

    // ── Unlocker mode (Mode page). User's chosen backend ────────────
    // "Ost" | "Bst" | "Custom". Older builds wrote "SteamTools" | "OpenSteamTools" |
    // "OpenSteamToolsNightly" | "CloudRedirect"; see ModeMigration, which rewrites those on startup.
    public string? SelectedMode { get; set; }

    // ── Install behavior ─────────────────────────────────────────────
    // When true (default), installs comment out setManifestid() lines and skip copying .manifest
    // files, so games aren't pinned to a version and Steam keeps them updated. Nullable so we can
    // tell "never set" (→ default ON) from an explicit user choice.
    public bool? AutoUpdateApps { get; set; }

    // When true (default), donate spare Steam decryption keys to the community pool. Nullable so
    // "never set" (→ default ON) is distinguishable from an explicit user choice.
    public bool? DonateKeys { get; set; }

    // Manage-page results-per-page. 0 = "All" (single infinite scroll). Nullable so "never set"
    // (→ default 24) is distinguishable from an explicit choice.
    public int? ManagePageSize { get; set; }

    // Fixes-page results-per-page. 0 = "All" (single infinite scroll). Nullable so "never set"
    // (→ default 24) is distinguishable from an explicit choice.
    public int? FixesPageSize { get; set; }

    // Builds-page game-list results-per-page. 0 = "All". Kept separate from ManagePageSize. The Builds
    // list is a narrow sidebar, so a size that suits the Manage grid rarely suits both.
    public int? BuildsPageSize { get; set; }

    // Add-page search results-per-page. 0 = "All".
    public int? AddPageSize { get; set; }

    // UI language as a BCP-47 tag ("en", "zh-Hans"). Null = follow the Windows display language.
    public string? Language { get; set; }

    // The user's own Hubcap (hubcapmanifest.com) API key ("smm_…"). Null = not configured; key-gated
    // sources stay locked until set. Stored locally. The app calls Hubcap directly with it.
    public string? HubcapApiKey { get; set; }

    // When true, register the app to launch on Windows sign-in (HKCU …\Run). Nullable so "never set"
    // (→ default OFF) is distinguishable from an explicit choice.
    public bool? StartWithWindows { get; set; }

    // When true, minimizing hides the window to the system tray instead of the taskbar. Nullable so
    // "never set" (→ default OFF) is distinguishable from an explicit choice.
    public bool? MinimizeToTray { get; set; }

    // The user's Steam Web API Key for fetching app list, store details, and cover art directly from Steam
    public string? SteamWebApiKey { get; set; }

    // Custom path to local tools directory (Steamless, SteamAutoCrack, DepotDownloaderMod)
    public string? ToolsPath { get; set; }

    // When true, FastFetch auto-picks the first available source and downloads immediately.
    // Nullable so "never set" (→ default OFF) is distinguishable from an explicit choice.
    public bool? FastFetch { get; set; }

    // DNS resolution preference: "Auto" | "Always" | "Never". Null = "Auto".
    public string? DnsMode { get; set; }
}

public class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUIGui");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private AppSettings _settings = new();

    public SettingsService() => Load();

    /// <summary>User-chosen Steam folder. Null = auto-detect from registry. Persisted only when set.</summary>
    public string? SteamPathOverride
    {
        get => _settings.SteamPathOverride;
        set
        {
            _settings.SteamPathOverride = string.IsNullOrWhiteSpace(value) ? null : value;
            Save();
        }
    }

    /// <summary>Selected unlocker backend ("SteamTools" | "OpenSteamTools"), or null if never chosen.</summary>
    public string? SelectedMode
    {
        get => _settings.SelectedMode;
        set { _settings.SelectedMode = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>When true (default), installs don't lock manifests so apps keep auto-updating.</summary>
    public bool AutoUpdateApps
    {
        get => _settings.AutoUpdateApps ?? true; // default ON
        set { _settings.AutoUpdateApps = value; Save(); }
    }

    /// <summary>When true, donate spare Steam decryption keys to the community pool (default OFF).</summary>
    public bool DonateKeys
    {
        get => _settings.DonateKeys ?? false; // default OFF
        set { _settings.DonateKeys = value; Save(); }
    }

    /// <summary>Manage-page results-per-page (default 24). 0 = "All" (single infinite scroll).</summary>
    public int ManagePageSize
    {
        get => _settings.ManagePageSize ?? 24; // default 24
        set { _settings.ManagePageSize = value; Save(); }
    }

    /// <summary>Fixes-page results-per-page (default 24). 0 = "All" (single infinite scroll).</summary>
    public int FixesPageSize
    {
        get => _settings.FixesPageSize ?? 24; // default 24
        set { _settings.FixesPageSize = value; Save(); }
    }

    /// <summary>Builds-page game-list results-per-page (default 10). 0 = "All". Smaller than the other
    /// pages' 24. This list is a narrow sidebar next to the build detail, not a full-width grid.</summary>
    public int BuildsPageSize
    {
        get => _settings.BuildsPageSize ?? 10; // default 10
        set { _settings.BuildsPageSize = value; Save(); }
    }

    /// <summary>Add-page search results-per-page (default 12). 0 = "All".</summary>
    public int AddPageSize
    {
        get => _settings.AddPageSize ?? 12; // default 12
        set { _settings.AddPageSize = value; Save(); }
    }

    /// <summary>UI language tag ("en" | "zh-Hans"), or null to follow the Windows display language.</summary>
    public string? Language
    {
        get => _settings.Language;
        set { _settings.Language = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>The user's Steam Web API Key, or null if not configured.</summary>
    public string? SteamWebApiKey
    {
        get => _settings.SteamWebApiKey;
        set { _settings.SteamWebApiKey = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>Custom path to local helper tools folder.</summary>
    public string? ToolsPath
    {
        get => _settings.ToolsPath;
        set { _settings.ToolsPath = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>The user's Hubcap API key ("smm_…"), or null if not configured.</summary>
    public string? HubcapApiKey
    {
        get => _settings.HubcapApiKey;
        set { _settings.HubcapApiKey = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>When true, the app is registered to launch on Windows sign-in (default OFF).</summary>
    public bool StartWithWindows
    {
        get => _settings.StartWithWindows ?? false; // default OFF
        set { _settings.StartWithWindows = value; Save(); }
    }

    /// <summary>When true, minimizing hides the window to the system tray (default OFF).</summary>
    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray ?? false; // default OFF
        set { _settings.MinimizeToTray = value; Save(); }
    }

    /// <summary>When true, FastFetch auto-picks the first available source and downloads immediately (default OFF).</summary>
    public bool FastFetch
    {
        get => _settings.FastFetch ?? false; // default OFF
        set { _settings.FastFetch = value; Save(); }
    }

    private static readonly string TmpPath = FilePath + ".tmp";
    private static readonly string BakPath = FilePath + ".bak";

    private void Load()
    {
        // Prefer the primary file; fall back to the last-good .bak. Crucially, NEVER silently reset a
        // corrupt-but-present file to defaults (a later Save would then overwrite it and lose real data).
        // Move it aside to .corrupt so it's preserved and can't be clobbered.
        if (TryLoad(FilePath)) return;
        PreserveCorrupt(FilePath);
        if (TryLoad(BakPath)) return;
        _settings = new AppSettings();
    }

    private bool TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) is { } loaded)
            {
                _settings = loaded;
                return true;
            }
        }
        catch { /* missing / truncated / invalid JSON → caller falls through */ }
        return false;
    }

    /// <summary>A present-but-unparseable settings file is moved aside (not deleted) so its contents survive
    /// for manual recovery and a subsequent Save can't overwrite it.</summary>
    private static void PreserveCorrupt(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch { /* best effort */ }
    }

    public string DnsMode
    {
        get => _settings.DnsMode ?? "Auto";
        set { _settings.DnsMode = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    private static bool IsEmpty(AppSettings s)
    {
        if (s.SteamPathOverride is not null) return false;
        if (s.SelectedMode is not null) return false;
        if (s.AutoUpdateApps is not null) return false;
        if (s.DonateKeys is not null) return false;
        if (s.ManagePageSize is not null) return false;
        if (s.FixesPageSize is not null) return false;
        if (s.BuildsPageSize is not null) return false;
        if (s.Language is not null) return false;
        if (s.HubcapApiKey is not null) return false;
        if (s.StartWithWindows is not null) return false;
        if (s.MinimizeToTray is not null) return false;
        if (s.FastFetch is not null) return false;
        if (s.DnsMode is not null) return false;
        return true;
    }

    private void Save()
    {
        if (IsEmpty(_settings))
        {
            foreach (var p in new[] { FilePath, BakPath, TmpPath })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            return;
        }

        Directory.CreateDirectory(Dir);
        string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(TmpPath, json);
        try { if (File.Exists(FilePath)) File.Copy(FilePath, BakPath, overwrite: true); } catch { }
        File.Move(TmpPath, FilePath, overwrite: true);
    }
}
