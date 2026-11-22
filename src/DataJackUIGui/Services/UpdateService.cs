using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using DataJackUIGui.Models;
using Velopack;
using Velopack.Sources;
using GhRelease = DataJackUIGui.Models.GithubRelease;
using GhAsset = DataJackUIGui.Models.GithubAsset;

namespace DataJackUIGui.Services;

/// <summary>
/// Silent background auto-update via Velopack + GitHub Releases. Checks on launch,
/// downloads the (delta) update in the background, and stages it to apply on next exit.
/// <para>
/// Resilience is two-layered: (1) <see cref="ProxiedFileDownloader"/> routes each repo's feed + package
/// downloads through GitHub mirrors for blocked/throttled regions (e.g. China); (2) it tries each repo in
/// <see cref="AppConfig.GithubReleasesRepos"/> in order, so if the PRIMARY repo is gone entirely
/// (banned / DMCA'd / account removed. Something the mirrors can't fix) it falls through to a backup repo.
/// </para>
/// <para>
/// Fallback: For portable or Inno Setup installations where Velopack is not installed (<c>!mgr.IsInstalled</c>),
/// it queries the latest release, downloads <c>DataJackUI-Update.zip</c>, stages to <c>%LocalAppData%\DataJackUI\pending_update\</c>,
/// and applies updates cleanly without terminal popups.
/// </para>
/// </summary>
public class UpdateService : IDisposable
{
    private UpdateManager? _manager;
    private readonly GithubProxy _githubProxy = new();

    private UpdateManager? GetManager()
    {
        if (_manager is not null) return _manager;
        try
        {
            var source = new SimpleWebSource(AppConfig.VelopackReleaseFeedUrl, new ProxiedFileDownloader());
            _manager = new UpdateManager(source);
        }
        catch
        {
            _manager = null;
        }
        return _manager;
    }

    // The manager whose feed produced the staged update. Apply against this same one.
    private UpdateManager? _stagedMgr;
    private UpdateInfo? _staged;

    // Direct binary update staged state (for non-Velopack / Inno Setup installations)
    private bool _hasDirectStagedUpdate;
    private string? _directStagedVersion;

    /// <summary>Raised on the thread pool when an update has finished downloading and is ready.</summary>
    public event Action? UpdateReady;

    /// <summary>True once an update is downloaded and waiting to be applied.</summary>
    public bool HasStagedUpdate => _staged is not null || _hasDirectStagedUpdate;

    /// <summary>Version of the staged update, if any.</summary>
    public string? StagedVersion => _staged?.TargetFullRelease.Version.ToString() ?? _directStagedVersion;

    /// <summary>Details of the staged update, if any (null if updated via direct binary fallback).</summary>
    public UpdateInfo? StagedUpdate => _staged;

    /// <summary>Prompt delegate for asking the user whether to update now or later. Defaults to WinUI 3 dialog via <see cref="UiMessageBox"/>.</summary>
    public Func<string, Task<bool>>? PromptUserForUpdate { get; set; }

    /// <summary>Periodic interval for recurring update checks (default: 6 hours).</summary>
    public TimeSpan PeriodicCheckInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>True if the periodic 6-hour background update timer is active.</summary>
    public bool IsPeriodicCheckRunning => _periodicCts is not null && !_periodicCts.IsCancellationRequested;

    private CancellationTokenSource? _periodicCts;

    // Test hooks for unit testing check, download, and restart workflows without live Velopack install
    internal Func<Task<string?>>? CustomCheckForUpdatesHandler { get; set; }
    internal Func<string, Task>? CustomDownloadUpdatesHandler { get; set; }
    internal Action<string[]?>? CustomApplyUpdatesHandler { get; set; }

    /// <summary>Directory where direct binary updates are staged.</summary>
    public static string PendingUpdateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DataJackUI",
        "pending_update");

    /// <summary>
    /// Checks for updates via Velopack's static release feed, falling back to direct binary update if Velopack is not installed.
    /// If an update is available, downloads the update in the background and stages it.
    /// </summary>
    /// <returns>UpdateInfo if a Velopack update was found and staged; otherwise null.</returns>
    public async Task<UpdateInfo?> CheckAndAutoUpdateAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // 1. Support custom test hook if set
        if (CustomCheckForUpdatesHandler is not null)
        {
            var targetVer = await CustomCheckForUpdatesHandler();
            if (string.IsNullOrWhiteSpace(targetVer)) return null;

            if (CustomDownloadUpdatesHandler is not null)
                await CustomDownloadUpdatesHandler(targetVer);

            UpdateReady?.Invoke();
            return null;
        }

        // 2. Production Velopack workflow via SimpleWebSource (static CDN, no scraping, no GitHub API limits)
        var mgr = GetManager();
        if (mgr != null && mgr.IsInstalled)
        {
            try
            {
                var info = await mgr.CheckForUpdatesAsync();
                if (info is not null)
                {
                    bool hasDelta = info.DeltasToTarget != null && info.DeltasToTarget.Length > 0;
                    PluginLog.Log($"[UpdateService] Update available -> Target: v{info.TargetFullRelease.Version}. Mode: {(hasDelta ? $"Delta ({info.DeltasToTarget!.Length} diffs, ~{info.DeltasToTarget[0].Size / 1024} KB)" : "Full package fallback (no delta match)")}");

                    // Downloads the delta package automatically (Velopack selects delta over full when available)
                    await mgr.DownloadUpdatesAsync(info, progress != null ? progress.Report : null, ct);
                    _stagedMgr = mgr;
                    _staged = info;
                    _hasDirectStagedUpdate = false;
                    _directStagedVersion = null;

                    PluginLog.Log($"[UpdateService] Successfully staged update v{info.TargetFullRelease.Version}.");
                    UpdateReady?.Invoke();
                    return info;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Log($"[UpdateService] Velopack CheckForUpdatesAsync failed: {ex.Message}. Falling back to direct binary update.");
            }
        }

        // 3. Direct binary update fallback (for Inno Setup / portable installations)
        try
        {
            await CheckAndDownloadDirectUpdateAsync(progress, ct);
        }
        catch (Exception ex)
        {
            PluginLog.Log($"[UpdateService] CheckAndDownloadDirectUpdateAsync error: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Fallback check and download for direct zip updates when Velopack is not installed or unavailable.
    /// </summary>
    public async Task<bool> CheckAndDownloadDirectUpdateAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var release = await FetchLatestReleaseAsync(ct);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName)) return false;

        var currentVer = GetCurrentAssemblyVersion();
        if (!TryParseVersion(release.TagName, out var latestVer) || latestVer <= currentVer)
        {
            return false;
        }

        PluginLog.Log($"[UpdateService] Direct update available: current v{currentVer} -> target v{latestVer}");

        // Find DataJackUI-Update.zip or DataJackUI-Portable.zip in the release assets
        var asset = release.Assets.FirstOrDefault(a =>
                        string.Equals(a.Name, "DataJackUI-Update.zip", StringComparison.OrdinalIgnoreCase))
                    ?? release.Assets.FirstOrDefault(a =>
                        string.Equals(a.Name, "DataJackUI-Portable.zip", StringComparison.OrdinalIgnoreCase));

        if (asset is null || string.IsNullOrWhiteSpace(asset.DownloadUrl))
        {
            PluginLog.Log("[UpdateService] Neither DataJackUI-Update.zip nor DataJackUI-Portable.zip found in latest release assets.");
            return false;
        }

        string stagingDir = PendingUpdateDir;
        string tempZip = Path.Combine(stagingDir, $"update_{Guid.NewGuid():N}.zip");

        try
        {
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
            }
            Directory.CreateDirectory(stagingDir);

            IProgress<double?>? proxyProgress = progress != null
                ? new Progress<double?>(val =>
                {
                    if (val.HasValue) progress.Report((int)Math.Clamp(val.Value * 100, 0, 100));
                })
                : null;

            PluginLog.Log($"[UpdateService] Downloading {asset.Name} from {asset.DownloadUrl}...");
            await _githubProxy.DownloadAsync(asset.DownloadUrl, tempZip, proxyProgress, ct);

            PluginLog.Log($"[UpdateService] Extracting update to {stagingDir}...");
            ZipFile.ExtractToDirectory(tempZip, stagingDir, overwriteFiles: true);

            _hasDirectStagedUpdate = true;
            _directStagedVersion = latestVer.ToString();
            PluginLog.Log($"[UpdateService] Successfully staged direct update v{latestVer} at {stagingDir}.");

            UpdateReady?.Invoke();
            return true;
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
    }

    private static Version GetCurrentAssemblyVersion()
    {
        return Assembly.GetEntryAssembly()?.GetName().Version
            ?? Assembly.GetExecutingAssembly().GetName().Version
            ?? new Version(0, 0, 0);
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        string raw = tag.TrimStart('v', 'V').Trim();
        int plusIdx = raw.IndexOf('+');
        if (plusIdx >= 0) raw = raw[..plusIdx];
        int dashIdx = raw.IndexOf('-');
        if (dashIdx >= 0) raw = raw[..dashIdx];

        return Version.TryParse(raw, out version!);
    }

    private async Task<GhRelease?> FetchLatestReleaseAsync(CancellationToken ct = default)
    {
        string url = "https://api.github.com/repos/NetrunnerGames/DataJackUI/releases/latest";
        try
        {
            using var res = await _githubProxy.SendAsync(url, ct);
            if (res is not null && res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync(ct);
                var rel = JsonSerializer.Deserialize<GhRelease>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (rel is not null) return rel;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Log($"[UpdateService] FetchLatestReleaseAsync via GitHub API failed: {ex.Message}");
        }

        // Secondary fallback: static releases.win.json from proxy
        try
        {
            string staticUrl = "https://proxy.netrunnergames.workers.dev/DataJackUI/releases.win.json";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var res = await http.GetAsync(staticUrl, ct);
            if (res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    string? maxVer = null;
                    Version? highestVer = null;
                    foreach (var item in assetsElem.EnumerateArray())
                    {
                        if (item.TryGetProperty("Version", out var vElem))
                        {
                            string vStr = vElem.GetString() ?? "";
                            if (TryParseVersion(vStr, out var parsed) && (highestVer == null || parsed > highestVer))
                            {
                                highestVer = parsed;
                                maxVer = vStr;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(maxVer))
                    {
                        var release = new GhRelease { TagName = "v" + maxVer };
                        // Direct download link pattern for release assets
                        release.Assets.Add(new GhAsset
                        {
                            Name = "DataJackUI-Update.zip",
                            DownloadUrl = $"https://github.com/NetrunnerGames/DataJackUI/releases/download/v{maxVer}/DataJackUI-Update.zip"
                        });
                        release.Assets.Add(new GhAsset
                        {
                            Name = "DataJackUI-Portable.zip",
                            DownloadUrl = $"https://github.com/NetrunnerGames/DataJackUI/releases/download/v{maxVer}/DataJackUI-Portable.zip"
                        });
                        return release;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Log($"[UpdateService] FetchLatestReleaseAsync via static releases.win.json failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>Check for, download, and stage an update in the background.</summary>
    public async Task CheckAndStageAsync()
    {
        await CheckAndAutoUpdateAsync();
    }

    /// <summary>
    /// Checks for updates. If a new release is found, prompts the user via a WinUI 3 dialog
    /// whether they wish to update now or later.
    /// If "Update Now" is selected, retrieves the package quickly, stages it, and restarts the app.
    /// If "Later" is selected, downloads and stages the update in the background for application on exit.
    /// </summary>
    /// <returns>True if an update was found and the user elected to update now; otherwise false.</returns>
    public async Task<bool> CheckForUpdatesAndPromptAsync()
    {
        // 1. Support custom test hook if set
        if (CustomCheckForUpdatesHandler is not null)
        {
            var targetVer = await CustomCheckForUpdatesHandler();
            if (string.IsNullOrWhiteSpace(targetVer)) return false;

            bool updateNow = PromptUserForUpdate is not null
                ? await PromptUserForUpdate(targetVer)
                : await DefaultPromptAsync(targetVer);

            if (updateNow)
            {
                if (CustomDownloadUpdatesHandler is not null)
                    await CustomDownloadUpdatesHandler(targetVer);

                UpdateReady?.Invoke();

                if (CustomApplyUpdatesHandler is not null)
                    CustomApplyUpdatesHandler(null);
                else
                    ApplyAndRestart();

                return true;
            }
            else
            {
                if (CustomDownloadUpdatesHandler is not null)
                    await CustomDownloadUpdatesHandler(targetVer);
                UpdateReady?.Invoke();
                return false;
            }
        }

        // 2. Production Velopack workflow
        var mgr = GetManager();
        if (mgr != null && mgr.IsInstalled)
        {
            try
            {
                var info = await mgr.CheckForUpdatesAsync();
                if (info is not null)
                {
                    string targetVersion = info.TargetFullRelease.Version.ToString();

                    bool updateNow = PromptUserForUpdate is not null
                        ? await PromptUserForUpdate(targetVersion)
                        : await DefaultPromptAsync(targetVersion);

                    await mgr.DownloadUpdatesAsync(info);
                    _stagedMgr = mgr;
                    _staged = info;
                    _hasDirectStagedUpdate = false;
                    _directStagedVersion = null;

                    if (updateNow)
                    {
                        UpdateReady?.Invoke();
                        ApplyAndRestart();
                        return true;
                    }
                    else
                    {
                        UpdateReady?.Invoke();
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Log($"[UpdateService] Velopack CheckForUpdatesAndPromptAsync error: {ex.Message}");
            }
        }

        // 3. Fallback direct update workflow
        try
        {
            var release = await FetchLatestReleaseAsync();
            if (release is not null && !string.IsNullOrWhiteSpace(release.TagName))
            {
                var currentVer = GetCurrentAssemblyVersion();
                if (TryParseVersion(release.TagName, out var latestVer) && latestVer > currentVer)
                {
                    string targetVersion = latestVer.ToString();
                    bool updateNow = PromptUserForUpdate is not null
                        ? await PromptUserForUpdate(targetVersion)
                        : await DefaultPromptAsync(targetVersion);

                    bool downloaded = await CheckAndDownloadDirectUpdateAsync();
                    if (downloaded)
                    {
                        if (updateNow)
                        {
                            ApplyAndRestart();
                            return true;
                        }
                        return false;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Log($"[UpdateService] Direct CheckForUpdatesAndPromptAsync fallback error: {ex.Message}");
        }

        return false;
    }

    private static async Task<bool> DefaultPromptAsync(string version)
    {
        return await UiMessageBox.ShowConfirmAsync(
            "Update Available",
            $"A new version of DataJackUI (v{version}) is available.\n\nWould you like to update now?",
            "Update Now",
            "Later");
    }

    /// <summary>
    /// Starts recurring background update checks (defaults to every 6 hours).
    /// </summary>
    public void StartPeriodicChecks(TimeSpan? interval = null)
    {
        StopPeriodicChecks();

        var period = interval ?? PeriodicCheckInterval;
        PeriodicCheckInterval = period;

        _periodicCts = new CancellationTokenSource();
        var ct = _periodicCts.Token;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(period);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(ct);
                    await CheckAndAutoUpdateAsync(ct: ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore transient errors and continue timer
                }
            }
        }, ct);
    }

    /// <summary>Stops the recurring update check timer.</summary>
    public void StopPeriodicChecks()
    {
        _periodicCts?.Cancel();
        _periodicCts?.Dispose();
        _periodicCts = null;
    }

    /// <summary>Apply the staged update now and relaunch into the new version. <paramref name="restartArgs"/>
    /// are passed to the relaunched process (e.g. the loader's --minimized --tray-locked) so the new
    /// instance keeps its session semantics.</summary>
    public void ApplyAndRestart(string[]? restartArgs = null)
    {
        if (CustomApplyUpdatesHandler is not null)
        {
            CustomApplyUpdatesHandler(restartArgs);
            return;
        }

        if (_stagedMgr is not null && _staged is not null)
        {
            var args = restartArgs ?? new[] { "--restarted" };
            _stagedMgr.ApplyUpdatesAndRestart(_staged.TargetFullRelease, args);
            return;
        }

        if (_hasDirectStagedUpdate)
        {
            ApplyDirectUpdateAndRestart(restartArgs);
        }
    }

    /// <summary>Apply the staged update after the app exits (no forced restart).</summary>
    public void ApplyOnExit()
    {
        if (_stagedMgr is not null && _staged is not null)
        {
            try
            {
                _stagedMgr.WaitExitThenApplyUpdates(_staged.TargetFullRelease, silent: true, restart: false);
            }
            catch { }
            return;
        }

        if (_hasDirectStagedUpdate)
        {
            ApplyDirectUpdateOnExit();
        }
    }

    private void ApplyDirectUpdateAndRestart(string[]? restartArgs)
    {
        string stagingDir = PendingUpdateDir;
        if (!Directory.Exists(stagingDir)) return;

        string targetDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        string exePath = Environment.ProcessPath
            ?? Path.Combine(targetDir, "DataJackUI.exe");

        string argsString = restartArgs is { Length: > 0 }
            ? string.Join(" ", restartArgs.Select(a => $"\"{a.Replace("\"", "\\\"")}\""))
            : "";

        int currentPid = Environment.ProcessId;

        // Use EncodedCommand with LiteralPath copy to safely handle paths with spaces, brackets, and quotes,
        // running silently with -WindowStyle Hidden and CreateNoWindow = true.
        string safeStaging = stagingDir.Replace("'", "''");
        string safeTarget = targetDir.Replace("'", "''");
        string safeExe = exePath.Replace("'", "''");
        string safeArgs = argsString.Replace("'", "''");

        string script =
            $"$pidToWait = {currentPid}; " +
            $"try {{ Wait-Process -Id $pidToWait -Timeout 15 -ErrorAction SilentlyContinue }} catch {{}}; " +
            $"Get-ChildItem -LiteralPath '{safeStaging}' -Force | ForEach-Object {{ Copy-Item -LiteralPath $_.FullName -Destination '{safeTarget}' -Recurse -Force }}; " +
            $"Remove-Item -LiteralPath '{safeStaging}' -Recurse -Force -ErrorAction SilentlyContinue; " +
            (string.IsNullOrEmpty(safeArgs)
                ? $"Start-Process -FilePath '{safeExe}'"
                : $"Start-Process -FilePath '{safeExe}' -ArgumentList '{safeArgs}'");

        string encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encodedCommand}",
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        try
        {
            PluginLog.Log($"[UpdateService] Launching direct updater script for target: {targetDir}");
            Process.Start(psi);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            PluginLog.Log($"[UpdateService] Failed to start direct updater script: {ex.Message}");
        }
    }

    private void ApplyDirectUpdateOnExit()
    {
        string stagingDir = PendingUpdateDir;
        if (!Directory.Exists(stagingDir)) return;

        string targetDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        int currentPid = Environment.ProcessId;

        string safeStaging = stagingDir.Replace("'", "''");
        string safeTarget = targetDir.Replace("'", "''");

        string script =
            $"$pidToWait = {currentPid}; " +
            $"try {{ Wait-Process -Id $pidToWait -Timeout 15 -ErrorAction SilentlyContinue }} catch {{}}; " +
            $"Get-ChildItem -LiteralPath '{safeStaging}' -Force | ForEach-Object {{ Copy-Item -LiteralPath $_.FullName -Destination '{safeTarget}' -Recurse -Force }}; " +
            $"Remove-Item -LiteralPath '{safeStaging}' -Recurse -Force -ErrorAction SilentlyContinue;";

        string encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encodedCommand}",
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        try
        {
            PluginLog.Log($"[UpdateService] Launching exit direct updater script for target: {targetDir}");
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            PluginLog.Log($"[UpdateService] Failed to start exit direct updater script: {ex.Message}");
        }
    }

    public void Dispose()
    {
        StopPeriodicChecks();
    }
}
