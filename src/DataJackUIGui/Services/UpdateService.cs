using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using AutoUpdaterDotNET;
using DataJackUIGui.Models;

namespace DataJackUIGui.Services;

/// <summary>
/// Background auto-update service powered by AutoUpdater.NET + Inno Setup.
/// Checks on launch and periodically in the background.
/// Downloads silently, verifies checksum, and prompts user with dark theme dialog to restart and apply.
/// </summary>
public class UpdateService : IDisposable
{
    private UpdateInfoEventArgs? _stagedArgs;
    private string? _downloadedInstallerPath;

    /// <summary>Raised on the thread pool when an update has finished downloading and is ready.</summary>
    public event Action? UpdateReady;

    /// <summary>True once an update is downloaded and waiting to be applied.</summary>
    public bool HasStagedUpdate => _stagedArgs is not null && !string.IsNullOrEmpty(_downloadedInstallerPath);

    /// <summary>Details of the staged update, if any.</summary>
    public UpdateInfoEventArgs? StagedUpdate => _stagedArgs;

    /// <summary>Prompt delegate for asking the user whether to update now or later.</summary>
    public Func<string, Task<bool>>? PromptUserForUpdate { get; set; }

    /// <summary>Periodic interval for recurring update checks (default: 6 hours).</summary>
    public TimeSpan PeriodicCheckInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>True if the periodic 6-hour background update timer is active.</summary>
    public bool IsPeriodicCheckRunning => _periodicCts is not null && !_periodicCts.IsCancellationRequested;

    private CancellationTokenSource? _periodicCts;

    // Test hooks for unit testing check, download, and restart workflows
    internal Func<Task<string?>>? CustomCheckForUpdatesHandler { get; set; }
    internal Func<string, Task>? CustomDownloadUpdatesHandler { get; set; }
    internal Action<string[]?>? CustomApplyUpdatesHandler { get; set; }

    public UpdateService()
    {
        ConfigureAutoUpdater();
    }

    private void ConfigureAutoUpdater()
    {
        // 1. Silent download & execution properties
        AutoUpdater.RunUpdateAsAdmin = false;
        AutoUpdater.DownloadPath = Path.Combine(Path.GetTempPath(), "DataJackUI_Updates");
        AutoUpdater.ClearAppDirectory = false;
        AutoUpdater.ShowSkipButton = true;
        AutoUpdater.ShowRemindLaterButton = true;
        AutoUpdater.Synchronous = false;
        
        // Pass /SILENT to Inno Setup installer so it shows a simple progress bar during install
        // CustomArgs /SILENT passed directly to Inno Setup process in ApplyAndRestart

        // JSON format parser
        AutoUpdater.ParseUpdateInfoEvent += AutoUpdaterOnParseUpdateInfoEvent;
    }

    private void AutoUpdaterOnParseUpdateInfoEvent(ParseUpdateInfoEventArgs args)
    {
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(args.RemoteData);
            var root = json.RootElement;

            string version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            string url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            string changelog = root.TryGetProperty("changelog", out var c) ? c.GetString() ?? "" : "";
            string checksum = root.TryGetProperty("checksum", out var cs) ? cs.GetString() ?? "" : "";

            args.UpdateInfo = new UpdateInfoEventArgs
            {
                CurrentVersion = version,
                ChangelogURL = changelog,
                DownloadURL = url,
                Mandatory = new Mandatory
                {
                    Value = false
                },
                CheckSum = !string.IsNullOrEmpty(checksum) ? new CheckSum
                {
                    Value = checksum,
                    HashingAlgorithm = "SHA256"
                } : null
            };
        }
        catch (Exception ex)
        {
            PluginLog.Log($"[UpdateService] Error parsing update JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks for updates silently in the background, downloads the installer, and stages it.
    /// </summary>
    public async Task<UpdateInfoEventArgs?> CheckAndAutoUpdateAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (CustomCheckForUpdatesHandler is not null)
        {
            var targetVer = await CustomCheckForUpdatesHandler();
            if (string.IsNullOrWhiteSpace(targetVer)) return null;

            if (CustomDownloadUpdatesHandler is not null)
                await CustomDownloadUpdatesHandler(targetVer);

            UpdateReady?.Invoke();
            return null;
        }

        try
        {
            var tcs = new TaskCompletionSource<UpdateInfoEventArgs?>();
            
            void CheckHandler(UpdateInfoEventArgs? args)
            {
                AutoUpdater.CheckForUpdateEvent -= CheckHandler;
                tcs.TrySetResult(args);
            }

            AutoUpdater.CheckForUpdateEvent += CheckHandler;
            AutoUpdater.Start(AppConfig.AutoUpdaterFeedUrl);

            var info = await tcs.Task;
            if (info is null || !info.IsUpdateAvailable) return null;

            PluginLog.Log($"[UpdateService] Update available -> Target: v{info.CurrentVersion}. Downloading silently...");

            // Download update installer to temp path
            string tempDir = AutoUpdater.DownloadPath;
            Directory.CreateDirectory(tempDir);
            string installerFile = Path.Combine(tempDir, Path.GetFileName(info.DownloadURL));
            if (string.IsNullOrEmpty(Path.GetExtension(installerFile)))
                installerFile += ".exe";

            // Download via ProxiedFileDownloader to route through Cloudflare Edge Cache & GitHub mirrors
            await ProxiedFileDownloader.DownloadFileAsync(info.DownloadURL, installerFile, progress, ct);

            // Verify SHA256 checksum if provided
            if (info.CheckSum != null && !string.IsNullOrEmpty(info.CheckSum.Value))
            {
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(installerFile);
                byte[] hash = sha.ComputeHash(stream);
                string computed = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(computed, info.CheckSum.Value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    PluginLog.Log($"[UpdateService] Checksum mismatch! Expected {info.CheckSum.Value}, got {computed}");
                    try { File.Delete(installerFile); } catch { }
                    return null;
                }
            }

            _stagedArgs = info;
            _downloadedInstallerPath = installerFile;
            PluginLog.Log($"[UpdateService] Successfully staged update v{info.CurrentVersion} at {installerFile}.");
            UpdateReady?.Invoke();
            return info;
        }
        catch (Exception ex)
        {
            PluginLog.Log($"[UpdateService] CheckAndAutoUpdateAsync error: {ex.Message}");
            return null;
        }
    }

    /// <summary>Check for, download, and stage an update in the background.</summary>
    public async Task CheckAndStageAsync()
    {
        await CheckAndAutoUpdateAsync();
    }

    /// <summary>
    /// Checks for updates. Prompts user if an update is found, or shows feedback if already up to date.
    /// </summary>
    public async Task<bool> CheckForUpdatesAndPromptAsync()
    {
        if (CustomCheckForUpdatesHandler is not null)
        {
            var targetVer = await CustomCheckForUpdatesHandler();
            if (string.IsNullOrWhiteSpace(targetVer)) return false;

            bool updateNow = PromptUserForUpdate is not null
                ? await PromptUserForUpdate(targetVer)
                : await DefaultPromptAsync(targetVer);

            if (CustomDownloadUpdatesHandler is not null)
                await CustomDownloadUpdatesHandler(targetVer);

            UpdateReady?.Invoke();

            if (updateNow)
            {
                if (CustomApplyUpdatesHandler is not null)
                    CustomApplyUpdatesHandler(null);
                else
                    ApplyAndRestart();

                return true;
            }
            return false;
        }

        var update = await CheckAndAutoUpdateAsync();
        if (update is null || !update.IsUpdateAvailable)
        {
            string currentVer = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "2.19.0";
            await UiMessageBox.ShowInfoAsync("Check for Updates", $"You are running the latest version of DataJackUI (v{currentVer}).");
            return false;
        }

        string targetVersion = update.CurrentVersion;
        bool promptResult = PromptUserForUpdate is not null
            ? await PromptUserForUpdate(targetVersion)
            : await DefaultPromptAsync(targetVersion);

        if (promptResult)
        {
            ApplyAndRestart();
            return true;
        }
        return false;
    }

    private static async Task<bool> DefaultPromptAsync(string version)
    {
        return await UiMessageBox.ShowConfirmAsync(
            "Update Available",
            $"DataJackUI v{version} has been downloaded and is ready to install.\n\nRestart now to complete the update?",
            "Restart Now",
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

    /// <summary>
    /// Launches the staged Inno Setup installer with /SILENT switch and terminates current process.
    /// </summary>
    public void ApplyAndRestart(string[]? restartArgs = null)
    {
        if (string.IsNullOrEmpty(_downloadedInstallerPath) || !File.Exists(_downloadedInstallerPath))
            return;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(_downloadedInstallerPath, "/SILENT")
            {
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });
        }
        catch (Exception ex)
        {
            PluginLog.Log($"[UpdateService] Error launching updater: {ex.Message}");
        }
    }

    /// <summary>Apply on exit if a staged update is ready.</summary>
    public void ApplyOnExit()
    {
        ApplyAndRestart();
    }

    public void Dispose()
    {
        StopPeriodicChecks();
    }
}
