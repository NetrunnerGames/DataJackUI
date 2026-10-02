using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataJackUIGui.Models;

namespace DataJackUIGui.Services;

/// <summary>
/// Manages the mutually-exclusive Steam unlockers (OpenSteamTools / BetterSteamTools / Custom). Only
/// one is active at a time. Each managed mode resolves its own build, verifies files by sha256, and
/// installs into the Steam root; Custom downloads and verifies nothing, since the user owns those
/// files. Switching overwrites shared files but doesn't delete the previous mode's leftovers. The
/// active mode persists in settings.
/// </summary>
public class UnlockerService(SteamService steam, SettingsService settings, CacheService cache, GithubProxy gh)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Per-mode cache of the GitHub release so re-opening the page doesn't hammer the API
    // (unauthenticated GitHub allows only 60 req/hr per IP). The "Check for updates" button forces a
    // fresh fetch (30s cooldown) for anyone who wants certainty sooner.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly Dictionary<UnlockerMode, (GithubRelease release, DateTime fetchedAt)> _releaseCache = new();
    private readonly Dictionary<UnlockerMode, (UpdateManifest manifest, DateTime fetchedAt)> _manifestCache = new();

    /// <summary>BetterSteamTools publishes its version + payload hash here instead of via the releases
    /// API. See <see cref="FetchUpdateManifestAsync"/>.</summary>
    private const string BstManifestUrl =
        "https://raw.githubusercontent.com/NetrunnerGames/DataJackUI/refs/heads/main/updates/opensteamtool/latest.toml";

    public IReadOnlyList<ModeDefinition> Modes { get; } =
    [
        // IceBreaker: DataJack native single-DLL Steam hook (version.dll) from NetrunnerGames/IceBreaker
        new(UnlockerMode.IceBreaker, "IceBreaker",
            Description: Resources.Strings.Mode_Desc_IceBreaker,
            Kind: ModeKind.Zip,
            Owner: "NetrunnerGames", Repo: "IceBreaker",
            FixedTag: null,
            PlaceFiles: ["version.dll"],
            ZipAssetPattern: null),

        // Upstream OpenSteamTool releases from OpenSteam001/OpenSteamTool
        new(UnlockerMode.Ost, "OpenSteamTools",
            Description: Resources.Strings.Mode_Desc_Ost,
            Kind: ModeKind.Zip,
            Owner: "OpenSteam001", Repo: "OpenSteamTool",
            FixedTag: null,
            PlaceFiles: ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"],
            ZipAssetPattern: "OpenSteamTool-{version}-Release.zip"),
    ];

    private ModeDefinition Def(UnlockerMode mode) => Modes.First(m => m.Mode == mode);

    /// <summary>The currently-active mode (the last one installed/selected), or null if none yet.</summary>
    public UnlockerMode? SelectedMode =>
        Enum.TryParse(settings.SelectedMode, out UnlockerMode m) ? m : null;

    /// <summary>Short display name of the active mode for status UI; null if none selected/detected yet.</summary>
    public string? SelectedModeDisplayName =>
        SelectedMode is { } m ? Def(m).DisplayName : null;

    /// <summary>
    /// Make sure the active OST/BST install is watching <c>config/stplug-in</c>, so luas written there
    /// hot-reload instead of needing a Steam restart.
    /// </summary>
    /// <remarks>
    /// The app no longer tells users to restart Steam after a lua change, because OST/BST re-read any
    /// directory listed in <c>opensteamtool.toml</c>'s <c>[lua] paths</c>. That makes this registration
    /// load-bearing rather than a nicety: previously it ran only inside <see cref="InstallAsync"/>, so a
    /// user who set their unlocker up outside this app got neither hot-reload nor restart advice.
    ///
    /// Safe to call repeatedly — the underlying edit is targeted, comment-preserving and append-only, and
    /// no-ops when the path is already present. Skipped for <c>Custom</c>, whose unlocker we know nothing
    /// about, and when no mode is selected.
    /// </remarks>
    public void EnsureLuaPathRegistered()
    {
        if (SelectedMode is not (UnlockerMode.Ost or UnlockerMode.IceBreaker)) return;
        if (steam.EffectivePath is not { } root) return;
        try { EnsureOpenSteamToolLuaPath(root); } catch { /* config tweak is best-effort */ }
    }

    // ── State query ─────────────────────────────────────────────────

    /// <summary>Query GitHub + local files → this mode's status. Returns Unknown on any failure/offline.
    /// Cached briefly unless <paramref name="forceRefresh"/>.</summary>
    public async Task<ModeState> GetStateAsync(UnlockerMode mode, bool forceRefresh = false, CancellationToken ct = default)
    {
        var def = Def(mode);
        bool active = SelectedMode == mode;

        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return new ModeState(mode, ModeStatus.Unknown, active, null);

        // IceBreaker: NetrunnerGames/IceBreaker releases (version.dll)
        if (mode == UnlockerMode.IceBreaker)
        {
            string local = Path.Combine(root, "version.dll");
            bool installed = File.Exists(local);
            var release = await FetchReleaseAsync(def, forceRefresh, ct);
            string? latestTag = release?.TagName ?? "v1.0a";
            ModeStatus status = !installed ? ModeStatus.NotInstalled : ModeStatus.UpToDate;
            if (installed && release is not null)
            {
                string? wanted = AssetDigest(release, "version.dll");
                if (wanted is not null && !AssetHash.OfFile(local).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    status = ModeStatus.UpdateAvailable;
            }
            return new ModeState(mode, status, active, latestTag);
        }

        // OST: recognise BOTH channels. An exact match against the nightly release means up to date;
        // an exact match against the stable "ost-" mirror means the user is on stable OST, which is
        // still OST, but this mode ships nightly, so offer them the move.
        if (mode == UnlockerMode.Ost)
        {
            var (ostStatus, latestTag) = await OstStatusAsync(def, root, forceRefresh, ct);
            return new ModeState(mode, ostStatus, active, latestTag);
        }

        return new ModeState(mode, ModeStatus.Unknown, active, null);
    }

    /// <summary>
    /// Status for a manifest-backed mode: the manifest names one payload file and its hash (for BST,
    /// OpenSteamTool.dll: the real change indicator; dwmapi/xinput are loaders that rarely move, so
    /// they're placed but not compared).
    /// </summary>
    private static ModeStatus ManifestStatus(UpdateManifest manifest, string root)
    {
        string local = Path.Combine(root, manifest.File);
        if (!File.Exists(local)) return ModeStatus.NotInstalled;
        return AssetHash.OfFile(local).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)
            ? ModeStatus.UpToDate
            : ModeStatus.UpdateAvailable;
    }

    /// <summary>
    /// OST status across both channels. Nightly builds differ per build in OpenSteamTool.dll, so that's
    /// the nightly indicator; stable OST is detected via the mendy-tools "ost-" mirror's per-DLL hashes
    /// (upstream only publishes a zip digest, not per-file ones).
    /// </summary>
    private async Task<(ModeStatus status, string? latestTag)> OstStatusAsync(
        ModeDefinition def, string root, bool forceRefresh, CancellationToken ct)
    {
        var nightly = await FetchReleaseAsync(def, forceRefresh, ct);
        string ostDll = Path.Combine(root, "OpenSteamTool.dll");

        if (nightly is not null && File.Exists(ostDll)
            && AssetDigest(nightly, "OpenSteamTool.dll") == AssetHash.OfFile(ostDll))
            return (ModeStatus.UpToDate, nightly.TagName);

        // Not the current nightly. Fall back to the stable mirror to tell "on stable OST" apart from
        // "nothing installed": both end up as UpdateAvailable, but only the former is really OST.
        var (mirrorStatus, mirrorTag) = await OstMirrorStatusAsync(root, ct);
        string? tag = nightly?.TagName ?? mirrorTag;

        if (mirrorStatus == ModeStatus.NotInstalled && !File.Exists(ostDll))
            return (ModeStatus.NotInstalled, tag);
        if (nightly is null && mirrorStatus == ModeStatus.Unknown)
            return (ModeStatus.Unknown, tag);
        return (ModeStatus.UpdateAvailable, tag);
    }

    private const string MirrorRepoOwner = "mendy-tools";
    private const string MirrorRepo = "verynotsusdllsthataredefnotstrelated";

    /// <summary>
    /// OpenSteamTools status via the mendy-tools "ost-" mirror (real per-DLL hashes). Hash the on-disk
    /// dwmapi.dll against the mirror: matches the LATEST ost- release (by published_at) → UpToDate;
    /// matches an older ost- release (or files present but no match) → UpdateAvailable; absent → NotInstalled.
    /// Returns the latest ost- tag for display.
    /// </summary>
    private async Task<(ModeStatus status, string? latestTag)> OstMirrorStatusAsync(string root, CancellationToken ct)
    {
        string dwmapi = Path.Combine(root, "dwmapi.dll");
        if (!File.Exists(dwmapi)) return (ModeStatus.NotInstalled, null);

        var releases = await FetchAllReleasesAsync(MirrorRepoOwner, MirrorRepo, null, ct);
        if (releases is null) return (ModeStatus.Unknown, null);

        var ost = releases.Where(r => r.TagName.StartsWith("ost-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue).ToList();
        if (ost.Count == 0) return (ModeStatus.Unknown, null);

        var latest = ost[0];
        string dwmHash = AssetHash.OfFile(dwmapi);

        if (AssetDigest(latest, "dwmapi.dll") == dwmHash) return (ModeStatus.UpToDate, latest.TagName);
        // Matches an older ost- release, or is present but unrecognized → an update exists.
        return (ModeStatus.UpdateAvailable, latest.TagName);
    }

    // ── Install / switch ─────────────────────────────────────────────

    /// <summary>Download + verify a mode's files, place them in the Steam root, remove the other mode's
    /// unique files, and persist the selection. Best-effort per file (locked files land in Failed).</summary>
    public async Task<ModeInstallResult> InstallAsync(
        UnlockerMode mode, IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        var def = Def(mode);
        if (def.Kind == ModeKind.Manual)
        {
            settings.SelectedMode = mode.ToString();
            return ModeInstallResult.Ok();
        }

        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail(Resources.Strings.Err_SteamNotFound);

        if (mode == UnlockerMode.IceBreaker)
            return await InstallIceBreakerFromReleaseAsync(root, def, progress, ct);

        var (version, zipUrl, zipName, wantedZipDigest, manifest) = await ResolveReleaseAsync(def, ct);
        if (version is null)
            return ModeInstallResult.Fail(zipUrl); // Error string passed through zipUrl on fail

        string staging = Path.Combine(Path.GetTempPath(), "DataJackUIGui", "mode", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);

            var (staged, zipDigest, err) = await StageAndVerifyFilesAsync(def, staging, zipUrl, zipName, wantedZipDigest, manifest, progress, ct);
            if (err is not null) return ModeInstallResult.Fail(err);

            var failed = CopyToSteamRoot(root, def, staged!);

            settings.SelectedMode = mode.ToString();
            cache.OpenSteamToolsInstalledZipDigest = zipDigest;
            cache.OpenSteamToolsInstalledVersion = version;
            try { EnsureOpenSteamToolLuaPath(root); } catch { }

            return failed.Count > 0
                ? new ModeInstallResult(false, string.Format(Resources.Strings.Err_WriteFailedCount, failed.Count), failed)
                : ModeInstallResult.Ok();
        }
        catch (OperationCanceledException) { return ModeInstallResult.Fail(Resources.Strings.Err_Cancelled); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
        finally { try { Directory.Delete(staging, recursive: true); } catch { } }
    }

    private async Task<ModeInstallResult> InstallIceBreakerFromReleaseAsync(
        string root, ModeDefinition def, IProgress<double?>? progress, CancellationToken ct)
    {
        var release = await FetchReleaseAsync(def, forceRefresh: true, ct);
        string staging = Path.Combine(Path.GetTempPath(), "DataJackUIGui", "icebreaker", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            string dest = Path.Combine(root, "version.dll");

            if (release is not null)
            {
                var dllAsset = FindAsset(release, "version.dll");
                var zipAsset = FindZipAsset(def, release);

                if (dllAsset is not null)
                {
                    string tmp = Path.Combine(staging, "version.dll");
                    await DownloadToFileAsync(dllAsset.DownloadUrl, tmp, progress, ct);
                    File.Copy(tmp, dest, overwrite: true);
                }
                else if (zipAsset is not null)
                {
                    string zipPath = Path.Combine(staging, zipAsset.Name);
                    await DownloadToFileAsync(zipAsset.DownloadUrl, zipPath, progress, ct);
                    var staged = ExtractWanted(zipPath, def.PlaceFiles, staging);
                    if (!staged.TryGetValue("version.dll", out string? stagedDll))
                        return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_DownloadMissingFiles, "version.dll"));
                    File.Copy(stagedDll, dest, overwrite: true);
                }
                else
                {
                    string localFallback = Path.Combine(AppContext.BaseDirectory, "Resources", "version.dll");
                    if (File.Exists(localFallback)) File.Copy(localFallback, dest, overwrite: true);
                    else return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_ReleaseMissingFile, "version.dll"));
                }
            }
            else
            {
                string localFallback = Path.Combine(AppContext.BaseDirectory, "Resources", "version.dll");
                if (File.Exists(localFallback)) File.Copy(localFallback, dest, overwrite: true);
                else return ModeInstallResult.Fail(Resources.Strings.Err_GithubUnreachable);
            }

            StampNow(dest);
            settings.SelectedMode = UnlockerMode.IceBreaker.ToString();
            try { EnsureOpenSteamToolLuaPath(root); } catch { }
            return ModeInstallResult.Ok();
        }
        catch (OperationCanceledException) { return ModeInstallResult.Fail(Resources.Strings.Err_Cancelled); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
        finally { try { Directory.Delete(staging, recursive: true); } catch { } }
    }

    private async Task<(string? version, string urlOrError, string zipName, string? wantedZipDigest, UpdateManifest? manifest)> ResolveReleaseAsync(ModeDefinition def, CancellationToken ct)
    {
        if (def.UpdateManifestUrl is not null)
        {
            var manifest = await FetchUpdateManifestAsync(def, forceRefresh: false, ct);
            if (manifest is null) return (null, Resources.Strings.Err_UpdateServerUnreachable, "", null, null);
            string zipName = (def.ZipAssetPattern ?? "").Replace("{version}", manifest.Version);
            string zipUrl = $"https://github.com/{def.Owner}/{def.Repo}/releases/download/{manifest.Version}/{zipName}";
            return (manifest.Version, zipUrl, zipName, null, manifest);
        }
        else
        {
            var release = await FetchReleaseAsync(def, forceRefresh: false, ct);
            if (release is null) return (null, Resources.Strings.Err_GithubUnreachable, "", null, null);
            var asset = FindZipAsset(def, release);
            if (asset is null) return (null, Resources.Strings.Err_ReleaseMissingDownload, "", null, null);
            return (release.TagName, asset.DownloadUrl, asset.Name, AssetHash.ParseDigest(asset.Digest), null);
        }
    }

    private async Task<(Dictionary<string, string>? staged, string? zipDigest, string? err)> StageAndVerifyFilesAsync(
        ModeDefinition def, string staging, string zipUrl, string zipName, string? wantedZipDigest, UpdateManifest? manifest, IProgress<double?>? progress, CancellationToken ct)
    {
        string zipPath = Path.Combine(staging, zipName);
        await DownloadToFileAsync(zipUrl, zipPath, progress, ct);

        string zipDigest = AssetHash.OfFile(zipPath);
        if (wantedZipDigest is { } want && !zipDigest.Equals(want, StringComparison.OrdinalIgnoreCase))
            return (null, null, Resources.Strings.Err_VerifyFailed);

        var staged = ExtractWanted(zipPath, def.PlaceFiles, staging);
        var missing = def.PlaceFiles.Where(f => !staged.ContainsKey(f)).ToList();
        if (missing.Count > 0)
            return (null, null, string.Format(Resources.Strings.Err_DownloadMissingFiles, string.Join(", ", missing)));

        if (manifest is not null && staged.TryGetValue(manifest.File, out string? payload)
            && !AssetHash.OfFile(payload).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            return (null, null, string.Format(Resources.Strings.Err_VerifyFailedFile, manifest.File));

        return (staged, zipDigest, null);
    }

    private List<string> CopyToSteamRoot(string root, ModeDefinition def, Dictionary<string, string> staged)
    {
        var failed = new List<string>();
        foreach (string file in def.PlaceFiles)
        {
            try
            {
                string dest = Path.Combine(root, file);
                File.Copy(staged[file], dest, overwrite: true);
                StampNow(dest);
            }
            catch { failed.Add(file); }
        }
        return failed;
    }

    // ── First-run auto-detect ────────────────────────────────────────

    /// <summary>
    /// One-time detection of an already-installed mode when none is selected yet. Hashes the on-disk
    /// DLLs against published digests, in priority order:
    ///   1. Bst: OpenSteamTool.dll vs the BST update manifest's sha256.
    ///   2. Ost (nightly): OpenSteamTool.dll vs any madoiscool/OST-Nightly release asset.
    ///   3. Ost (stable): dwmapi.dll AND xinput1_4.dll vs mendy-tools tag "ost-" (loose-DLL mirror;
    ///      OST ships a zip whose API digest isn't per-DLL, so we mirror the DLLs for hash-matching).
    ///      Still OST, just the other channel: GetStateAsync will offer the move to nightly.
    ///
    /// EVERY branch requires an EXACT hash match. Do not relax this to "the file exists": SteamTools
    /// shipped the same dwmapi.dll / xinput1_4.dll filenames, so a presence check would silently claim
    /// ex-SteamTools users as OST. Exactly the users ModeMigration deliberately routes to onboarding.
    /// Never auto-selects <see cref="UnlockerMode.Custom"/>; that's an explicit user choice.
    ///
    /// Persists the match as the active mode. Returns the detected mode, or null if nothing matched.
    /// </summary>
    public async Task<UnlockerMode?> DetectActiveModeAsync(CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid) return null;

        UnlockerMode? detected = await DetectIceBreaker(root)
                              ?? DetectOst(root)
                              ?? await DetectStableOst(root, ct);

        if (detected is { } m) settings.SelectedMode = m.ToString();
        return detected;
    }

    private Task<UnlockerMode?> DetectIceBreaker(string root)
    {
        return Task.FromResult(File.Exists(Path.Combine(root, "version.dll")) ? (UnlockerMode?)UnlockerMode.IceBreaker : null);
    }

    private UnlockerMode? DetectOst(string root)
    {
        return File.Exists(Path.Combine(root, "OpenSteamTool.dll")) ? UnlockerMode.Ost : null;
    }

    private async Task<UnlockerMode?> DetectStableOst(string root, CancellationToken ct)
    {
        string dwmapi = Path.Combine(root, "dwmapi.dll");
        string xinput = Path.Combine(root, "xinput1_4.dll");
        if (!File.Exists(dwmapi) || !File.Exists(xinput)) return null;

        var mirror = await FetchAllReleasesAsync(MirrorRepoOwner, MirrorRepo, null, ct);
        var tagged = mirror?.Where(r => r.TagName.StartsWith("ost-", StringComparison.OrdinalIgnoreCase)).ToList();
        
        if (tagged is { Count: > 0 })
        {
            string dwmHash = AssetHash.OfFile(dwmapi);
            string xinHash = AssetHash.OfFile(xinput);
            if (tagged.Any(r => AssetDigest(r, "dwmapi.dll") == dwmHash) && tagged.Any(r => AssetDigest(r, "xinput1_4.dll") == xinHash))
                return UnlockerMode.Ost;
        }
        return null;
    }

    /// <summary>Digest (hex, no prefix) of a release's same-named asset, or null if absent.</summary>
    private static string? AssetDigest(GithubRelease r, string assetName) =>
        AssetHash.ParseDigest(r.Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase))?.Digest);

    /// <summary>The same-named asset, or null if this release doesn't have it.</summary>
    private static GithubAsset? FindAsset(GithubRelease r, string assetName) =>
        r.Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fetch every release for a repo (per_page=100). If <paramref name="tag"/> is set, only
    /// that one release (wrapped in a list). Null on failure/offline.</summary>
    private async Task<List<GithubRelease>?> FetchAllReleasesAsync(string owner, string repo, string? tag, CancellationToken ct)
    {
        string url = tag is not null
            ? $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}"
            : $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=100";
        try
        {
            // Routed via GithubProxy: direct, then mirrors (for blocked/throttled regions).
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            string body = await res.Content.ReadAsStringAsync(ct);
            if (tag is not null)
            {
                var one = JsonSerializer.Deserialize<GithubRelease>(body, JsonOpts);
                return one is null ? null : [one];
            }
            return JsonSerializer.Deserialize<List<GithubRelease>>(body, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    // ── OpenSteamTool config ─────────────────────────────────────────

    private const string OstLuaPath = "config/stplug-in";

    /// <summary>
    /// Ensure &lt;Steam&gt;/opensteamtool.toml's [lua] paths array contains "config/stplug-in" so our luas
    /// are loaded. Creates the file/section/array if missing; appends without removing existing paths.
    /// Targeted text edit (preserves comments and other sections). Commented-out lines are ignored.
    /// </summary>
    private static void EnsureOpenSteamToolLuaPath(string steamRoot)
    {
        EnsureDataJackJsonLuaPath(steamRoot);

        string tomlPath = Path.Combine(steamRoot, "opensteamtool.toml");
        if (!File.Exists(tomlPath))
        {
            File.WriteAllText(tomlPath, $"[lua]\npaths = [\"{OstLuaPath}\"]\n\n[cloud]\nenabled = false\n");
            return;
        }

        var lines = File.ReadAllLines(tomlPath).ToList();
        int luaHeader = lines.FindIndex(l => IsActiveTableHeader(l, "lua"));
        if (luaHeader < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[lua]");
            lines.Add($"paths = [\"{OstLuaPath}\"]");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        int sectionEnd = lines.FindIndex(luaHeader + 1, IsActiveAnyTableHeader);
        if (sectionEnd < 0) sectionEnd = lines.Count;

        int pathsStart = FindPathsArray(lines, luaHeader, sectionEnd);
        if (pathsStart < 0)
        {
            lines.Insert(luaHeader + 1, $"paths = [\"{OstLuaPath}\"]");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        int pathsEnd = pathsStart;
        while (pathsEnd < sectionEnd && !lines[pathsEnd].Contains(']')) pathsEnd++;
        if (pathsEnd >= sectionEnd) pathsEnd = sectionEnd - 1;

        string block = string.Join("\n", lines.GetRange(pathsStart, pathsEnd - pathsStart + 1));
        if (Regex.IsMatch(block, @"[""']\s*" + Regex.Escape(OstLuaPath).Replace("/", @"[/\\]+") + @"\s*[""']", RegexOptions.IgnoreCase))
            return;

        int closeLine = pathsEnd;
        string line = lines[closeLine];
        int bracket = line.LastIndexOf(']');
        string before = line[..bracket].TrimEnd();
        bool arrayEmpty = Regex.IsMatch(before, @"\[\s*$");
        string newBefore = arrayEmpty ? before + $" \"{OstLuaPath}\"" : before + $", \"{OstLuaPath}\"";
        lines[closeLine] = newBefore + line[bracket..];

        File.WriteAllLines(tomlPath, lines);
    }

    private static int FindPathsArray(List<string> lines, int luaHeader, int sectionEnd)
    {
        for (int i = luaHeader + 1; i < sectionEnd; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;
            if (Regex.IsMatch(t, @"^paths\s*=")) return i;
        }
        return -1;
    }

    /// <summary>True if the line is an active (uncommented) [name] table header.</summary>
    private static bool IsActiveTableHeader(string line, string name)
    {
        string t = line.TrimStart();
        return !t.StartsWith('#') && Regex.IsMatch(t, $@"^\[\s*{Regex.Escape(name)}\s*\]");
    }

    /// <summary>True if the line is any active (uncommented) [..] table header.</summary>
    private static bool IsActiveAnyTableHeader(string line)
    {
        string t = line.TrimStart();
        return !t.StartsWith('#') && Regex.IsMatch(t, @"^\[[^\[].*\]");
    }

    // ── CloudRedirect add-on (a feature of the OpenSteamTool Nightly build) ──────────
    // Not a mutually-exclusive mode: it drops cloud_redirect.dll into the Steam root and toggles
    // [cloud] enabled in opensteamtool.toml (parallel to how BST install writes [lua] paths). Only
    // meaningful when the Nightly BST mode is active.

    private const string CloudRedirectDll = "cloud_redirect.dll";
    private (GithubRelease release, DateTime fetchedAt)? _crReleaseCache;

    private async Task<GithubRelease?> FetchCloudRedirectReleaseAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _crReleaseCache is { } c && DateTime.UtcNow - c.fetchedAt < CacheTtl)
            return c.release;

        string url = $"https://api.github.com/repos/{AppConfig.CloudRedirectRepo}/releases/latest";
        try
        {
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (release is not null) _crReleaseCache = (release, DateTime.UtcNow);
            return release;
        }
        catch { return null; }
    }

    /// <summary>Add-on state from disk (dll present + [cloud] enabled) plus, when <paramref name="checkUpdate"/>
    /// and installed, whether a newer cloud_redirect.dll is published.</summary>
    public async Task<CloudRedirectAddonState> GetCloudRedirectStateAsync(
        bool checkUpdate, bool forceRefresh = false, CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return new CloudRedirectAddonState(false, false, false, null);

        string dll = Path.Combine(root, CloudRedirectDll);
        bool installed = File.Exists(dll);
        bool enabled = ReadOpenSteamToolCloudEnabled(root);

        bool updateAvailable = false;
        string? latest = null;
        if (checkUpdate && installed)
        {
            var release = await FetchCloudRedirectReleaseAsync(forceRefresh, ct);
            if (release is not null)
            {
                latest = release.TagName;
                string? wanted = AssetDigest(release, CloudRedirectDll);
                if (wanted is not null && !AssetHash.OfFile(dll).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    updateAvailable = true;
            }
        }
        return new CloudRedirectAddonState(installed, enabled, updateAvailable, latest);
    }

    /// <summary>Enable: download cloud_redirect.dll if missing (verified), then set [cloud] enabled = true.
    /// Takes effect on the next Steam launch.</summary>
    public async Task<ModeInstallResult> EnableCloudRedirectAsync(IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail(Resources.Strings.Err_SteamNotFound);

        if (!File.Exists(Path.Combine(root, CloudRedirectDll)))
        {
            var dl = await DownloadCloudRedirectDllAsync(root, progress, ct);
            if (!dl.Success) return dl;
        }
        try { SetOpenSteamToolCloudEnabled(root, true); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
        return ModeInstallResult.Ok();
    }

    /// <summary>Disable: flip [cloud] enabled = false (keeps the dll on disk).</summary>
    public ModeInstallResult DisableCloudRedirect()
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail(Resources.Strings.Err_SteamNotFound);
        try { SetOpenSteamToolCloudEnabled(root, false); return ModeInstallResult.Ok(); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
    }

    /// <summary>Update: replace cloud_redirect.dll with the latest (verified). Fails with a "close Steam"
    /// message if Steam holds the existing dll open.</summary>
    public async Task<ModeInstallResult> UpdateCloudRedirectAsync(IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail(Resources.Strings.Err_SteamNotFound);
        return await DownloadCloudRedirectDllAsync(root, progress, ct);
    }

    private async Task<ModeInstallResult> DownloadCloudRedirectDllAsync(string root, IProgress<double?>? progress, CancellationToken ct)
    {
        var release = await FetchCloudRedirectReleaseAsync(forceRefresh: true, ct);
        if (release is null) return ModeInstallResult.Fail(Resources.Strings.Err_GithubUnreachable);
        var asset = FindAsset(release, CloudRedirectDll);
        if (asset is null) return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_ReleaseMissingFile, CloudRedirectDll));

        string staging = Path.Combine(Path.GetTempPath(), "DataJackUIGui", "cloud", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            string tmp = Path.Combine(staging, CloudRedirectDll);
            await DownloadToFileAsync(asset.DownloadUrl, tmp, progress, ct);
            if (AssetHash.ParseDigest(asset.Digest) is { } want && !AssetHash.OfFile(tmp).Equals(want, StringComparison.OrdinalIgnoreCase))
                return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_VerifyFailedFile, CloudRedirectDll));

            try
            {
                string dest = Path.Combine(root, CloudRedirectDll);
                File.Copy(tmp, dest, overwrite: true);
                StampNow(dest);
            }
            catch
            {
                // Steam has the loaded dll locked: surface a close-Steam message (same as mode install).
                return ModeInstallResult.Fail(string.Format(Resources.Strings.Err_WriteFailedFile, CloudRedirectDll));
            }
            return ModeInstallResult.Ok();
        }
        catch (OperationCanceledException) { return ModeInstallResult.Fail(Resources.Strings.Err_Cancelled); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
        finally { try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>Ensure opensteamtool.toml or datajack.json has an active [cloud] section with enabled = true|false.</summary>
    private static void SetOpenSteamToolCloudEnabled(string steamRoot, bool enabled)
    {
        SetDataJackJsonCloudEnabled(steamRoot, enabled);

        string tomlPath = Path.Combine(steamRoot, "opensteamtool.toml");
        string val = enabled ? "true" : "false";

        if (!File.Exists(tomlPath))
        {
            File.WriteAllText(tomlPath, $"[cloud]\nenabled = {val}\n");
            return;
        }

        var lines = File.ReadAllLines(tomlPath).ToList();

        int header = lines.FindIndex(l => IsActiveTableHeader(l, "cloud"));
        if (header < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[cloud]");
            lines.Add($"enabled = {val}");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        int sectionEnd = lines.FindIndex(header + 1, IsActiveAnyTableHeader);
        if (sectionEnd < 0) sectionEnd = lines.Count;

        for (int i = header + 1; i < sectionEnd; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;                       // commented → ignore
            if (Regex.IsMatch(t, @"^enabled\s*="))
            {
                string indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                lines[i] = $"{indent}enabled = {val}";
                File.WriteAllLines(tomlPath, lines);
                return;
            }
        }

        // [cloud] exists but no active enabled key → insert one under the header.
        lines.Insert(header + 1, $"enabled = {val}");
        File.WriteAllLines(tomlPath, lines);
    }

    /// <summary>Read datajack.json or opensteamtool.toml's active [cloud] enabled value (false if absent).</summary>
    private static bool ReadOpenSteamToolCloudEnabled(string steamRoot)
    {
        string jsonPath = Path.Combine(steamRoot, "datajack.json");
        if (File.Exists(jsonPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                if (doc.RootElement.TryGetProperty("cloud", out var cloudObj) &&
                    cloudObj.TryGetProperty("enabled", out var enabledProp))
                {
                    return enabledProp.GetBoolean();
                }
            }
            catch { /* fallback to toml */ }
        }

        string tomlPath = Path.Combine(steamRoot, "opensteamtool.toml");
        if (!File.Exists(tomlPath)) return false;

        var lines = File.ReadAllLines(tomlPath);
        int header = Array.FindIndex(lines, l => IsActiveTableHeader(l, "cloud"));
        if (header < 0) return false;

        for (int i = header + 1; i < lines.Length; i++)
        {
            if (IsActiveAnyTableHeader(lines[i])) break;            // next section → done
            string t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;                       // commented → ignore
            var m = Regex.Match(t, @"^enabled\s*=\s*(\w+)");
            if (m.Success) return m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static void SetDataJackJsonCloudEnabled(string steamRoot, bool enabled)
    {
        try
        {
            string jsonPath = Path.Combine(steamRoot, "datajack.json");
            Dictionary<string, object> dict = new();
            if (File.Exists(jsonPath))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(jsonPath));
                    if (existing is not null) dict = existing;
                }
                catch { }
            }

            Dictionary<string, object> cloudSection = new();
            if (dict.TryGetValue("cloud", out var rawCloud) && rawCloud is JsonElement el && el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                    cloudSection[prop.Name] = prop.Value.ValueKind == JsonValueKind.True ? true :
                                              prop.Value.ValueKind == JsonValueKind.False ? false :
                                              prop.Value.ToString();
            }

            cloudSection["enabled"] = enabled;
            if (!cloudSection.ContainsKey("library")) cloudSection["library"] = "cloud_redirect.dll";
            dict["cloud"] = cloudSection;

            string json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json);
        }
        catch { /* best effort */ }
    }

    private static void EnsureDataJackJsonLuaPath(string steamRoot)
    {
        try
        {
            string jsonPath = Path.Combine(steamRoot, "datajack.json");
            Dictionary<string, object> dict = new();
            if (File.Exists(jsonPath))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(jsonPath));
                    if (existing is not null) dict = existing;
                }
                catch { }
            }

            Dictionary<string, object> luaSection = new();
            List<string> defaultLuaPaths = new() { "config/stplug-in", "config/lua", "config/datalua" };
            List<string> paths = new(defaultLuaPaths);

            if (dict.TryGetValue("lua", out var rawLua) && rawLua is JsonElement el && el.ValueKind == JsonValueKind.Object)
            {
                EnsureJsonHasLuaPath(el, luaSection, paths);
            }

            luaSection["enabled"] = true;
            luaSection["paths"] = paths;
            dict["lua"] = luaSection;

            string json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json);
        }
        catch { /* best effort */ }
    }

    private static void EnsureJsonHasLuaPath(JsonElement el, Dictionary<string, object> luaSection, List<string> paths)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.NameEquals("paths") && prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prop.Value.EnumerateArray())
                {
                    string s = item.GetString() ?? "";
                    if (!string.IsNullOrEmpty(s) && !paths.Contains(s))
                        paths.Add(s);
                }
            }
            else
            {
                luaSection[prop.Name] = prop.Value.ValueKind == JsonValueKind.True ? true :
                                        prop.Value.ValueKind == JsonValueKind.False ? false :
                                        prop.Value.ToString();
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private async Task<GithubRelease?> FetchReleaseAsync(ModeDefinition def, bool forceRefresh, CancellationToken ct)
    {
        // Serve from cache within the TTL unless a forced refresh is requested.
        if (!forceRefresh
            && _releaseCache.TryGetValue(def.Mode, out var cached)
            && DateTime.UtcNow - cached.fetchedAt < CacheTtl)
            return cached.release;

        string url = def.FixedTag is not null
            ? $"https://api.github.com/repos/{def.Owner}/{def.Repo}/releases/tags/{def.FixedTag}"
            : $"https://api.github.com/repos/{def.Owner}/{def.Repo}/releases/latest";
        try
        {
            // Routed via GithubProxy: direct, then mirrors (for blocked/throttled regions).
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (release is not null) _releaseCache[def.Mode] = (release, DateTime.UtcNow);
            return release;
        }
        catch
        {
            return null; // offline / rate-limited / parse error → caller maps to Unknown
        }
    }

    /// <summary>
    /// Fetch a mode's <c>latest.toml</c> update manifest. Version + payload filename + sha256.
    ///
    /// This is deliberately NOT an api.github.com call: it's a raw-hosted file, so a manifest-backed
    /// mode never spends any of the 60 req/hr unauthenticated GitHub API budget. It also gives a real
    /// per-file hash, which is the problem the "ost-" mirror repo exists to work around for the
    /// release-API modes. Routed through GithubProxy all the same. IsGithub covers
    /// raw.githubusercontent.com, so blocked regions still fall through to the mirrors.
    ///
    /// Shares the release cache's TTL, keyed by mode.
    /// </summary>
    private async Task<UpdateManifest?> FetchUpdateManifestAsync(ModeDefinition def, bool forceRefresh, CancellationToken ct)
    {
        if (def.UpdateManifestUrl is null) return null;
        if (!forceRefresh
            && _manifestCache.TryGetValue(def.Mode, out var cached)
            && DateTime.UtcNow - cached.fetchedAt < CacheTtl)
            return cached.manifest;

        try
        {
            using var res = await gh.SendAsync(def.UpdateManifestUrl, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            var manifest = ParseUpdateManifest(await res.Content.ReadAsStringAsync(ct));
            if (manifest is not null) _manifestCache[def.Mode] = (manifest, DateTime.UtcNow);
            return manifest;
        }
        catch
        {
            return null; // offline / parse error → caller maps to Unknown
        }
    }

    /// <summary>
    /// Read the three keys we care about out of a flat <c>key = "value"</c> TOML. Hand-rolled on
    /// purpose. The file has no tables, arrays or nesting, so a TOML package would be a dependency
    /// bought for three lines of parsing.
    /// </summary>
    private static UpdateManifest? ParseUpdateManifest(string toml)
    {
        string? version = null, path = null, sha = null;
        foreach (string raw in toml.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim().Trim('"');
            switch (key)
            {
                case "version": version = value; break;
                case "path": path = value; break;
                case "sha256": sha = value; break;
            }
        }

        if (version is null or "" || path is null or "" || sha is null or "") return null;
        // `path` is repo-relative ("opensteamtool/v1.0.0/OpenSteamTool.dll"); only the filename matters
        // to us, since that's what gets compared in the Steam root.
        return new UpdateManifest(version, Path.GetFileName(path), sha.ToLowerInvariant());
    }

    /// <summary>Find the small Release zip (matches the pattern, excludes any Debug build).</summary>
    private static GithubAsset? FindZipAsset(ModeDefinition def, GithubRelease release)
    {
        string wanted = (def.ZipAssetPattern ?? "").Replace("{version}", release.TagName);
        return release.Assets.FirstOrDefault(a =>
                   a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) &&
                   !a.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase))
               ?? release.Assets.FirstOrDefault(a =>
                   a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                   a.Name.Contains("Release", StringComparison.OrdinalIgnoreCase) &&
                   !a.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Extract just the wanted files from a zip into <paramref name="destDir"/> (flattened).</summary>
    private static Dictionary<string, string> ExtractWanted(string zipPath, string[] wanted, string destDir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            string? match = wanted.FirstOrDefault(w => w.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null || result.ContainsKey(match)) continue;
            string dest = Path.Combine(destDir, match);
            entry.ExtractToFile(dest, overwrite: true);
            result[match] = dest;
        }
        return result;
    }

    // Asset download routed via GithubProxy: direct, then mirrors (for blocked/throttled regions).
    private Task DownloadToFileAsync(string url, string destPath, IProgress<double?>? progress, CancellationToken ct) =>
        gh.DownloadAsync(url, destPath, progress, ct);

    private static void StampNow(string path)
    {
        try
        {
            var now = DateTime.Now;
            File.SetCreationTime(path, now);
            File.SetLastWriteTime(path, now);
        }
        catch { /* cosmetic */ }
    }
}
