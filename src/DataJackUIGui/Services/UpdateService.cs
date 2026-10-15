using Velopack;
using Velopack.Sources;

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
/// </summary>
public class UpdateService : IDisposable
{
    private UpdateManager[]? _managers;

    private UpdateManager[] GetManagers()
    {
        if (_managers is not null) return _managers;
        try
        {
            _managers = AppConfig.GithubReleasesRepos
                .Select(repo => new UpdateManager(
                    new GithubSource(repo, accessToken: null, prerelease: false,
                        downloader: new ProxiedFileDownloader())))
                .ToArray();
        }
        catch
        {
            _managers = Array.Empty<UpdateManager>();
        }
        return _managers;
    }

    // The manager whose repo actually produced the staged update. Apply against this same one.
    private UpdateManager? _stagedMgr;
    private UpdateInfo? _staged;

    /// <summary>Raised on the thread pool when an update has finished downloading and is ready.</summary>
    public event Action? UpdateReady;

    /// <summary>True once an update is downloaded and waiting to be applied.</summary>
    public bool HasStagedUpdate => _staged is not null;

    /// <summary>Details of the staged update, if any.</summary>
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

    /// <summary>Check for, download, and stage an update. Tries each repo in order until one yields a
    /// usable update; the first success wins. No-op for un-installed (dev) builds.</summary>
    public async Task CheckAndStageAsync()
    {
        var managers = GetManagers();
        if (managers.Length == 0 || !managers[0].IsInstalled) return; // `dotnet run` / unpacked builds

        foreach (var mgr in managers)
        {
            try
            {
                var info = await mgr.CheckForUpdatesAsync();
                if (info is null) return;
                await mgr.DownloadUpdatesAsync(info);
                _stagedMgr = mgr;
                _staged = info;
                UpdateReady?.Invoke();
                return;
            }
            catch
            {
                // This repo is unreachable/gone (or a download failed). Fall through to the next backup.
            }
        }
        // Every repo failed (offline, or all repos down). Fail silently, retry next launch.
    }

    /// <summary>
    /// Checks for updates. If a new release is found, prompts the user via a WinUI 3 dialog
    /// whether they wish to update now or later.
    /// If "Update Now" is selected, retrieves the delta package quickly, stages it, and restarts the app.
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
                // Background staging on "Later"
                if (CustomDownloadUpdatesHandler is not null)
                    await CustomDownloadUpdatesHandler(targetVer);
                UpdateReady?.Invoke();
                return false;
            }
        }

        // 2. Production Velopack workflow
        var managers = GetManagers();
        if (managers.Length == 0 || !managers[0].IsInstalled) return false; // dev builds

        foreach (var mgr in managers)
        {
            try
            {
                var info = await mgr.CheckForUpdatesAsync();
                if (info is null) continue;

                string targetVersion = info.TargetFullRelease.Version.ToString();

                bool updateNow = PromptUserForUpdate is not null
                    ? await PromptUserForUpdate(targetVersion)
                    : await DefaultPromptAsync(targetVersion);

                if (updateNow)
                {
                    // Retrieves delta nupkg to update the app quickly
                    await mgr.DownloadUpdatesAsync(info);
                    _stagedMgr = mgr;
                    _staged = info;
                    UpdateReady?.Invoke();
                    ApplyAndRestart();
                    return true;
                }
                else
                {
                    // User elected "Later". Download and stage in the background without forcing restart.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await mgr.DownloadUpdatesAsync(info);
                            _stagedMgr = mgr;
                            _staged = info;
                            UpdateReady?.Invoke();
                        }
                        catch { }
                    });
                    return false;
                }
            }
            catch
            {
                // Fall through to backup repo
            }
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
                    await CheckForUpdatesAndPromptAsync();
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
        if (_stagedMgr is not null && _staged is not null)
            _stagedMgr.ApplyUpdatesAndRestart(_staged, restartArgs);
    }

    /// <summary>Apply the staged update after the app exits (no forced restart).</summary>
    public void ApplyOnExit()
    {
        if (_stagedMgr is not null && _staged is not null)
            _stagedMgr.WaitExitThenApplyUpdates(_staged, silent: true, restart: false);
    }

    public void Dispose()
    {
        StopPeriodicChecks();
    }
}
