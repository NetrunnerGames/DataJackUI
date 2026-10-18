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
    private UpdateManager? _manager;

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

    /// <summary>
    /// Checks for updates via Velopack's static release feed.
    /// If an update is available, downloads the delta package in the background,
    /// and stages it to apply automatically on application exit (without requiring manual restart).
    /// </summary>
    /// <returns>UpdateInfo if a new update was found and staged; otherwise null.</returns>
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
        if (mgr is null || !mgr.IsInstalled) return null;

        try
        {
            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) return null;

            // Downloads the delta package automatically (Velopack selects delta over full when available)
            await mgr.DownloadUpdatesAsync(info, progress != null ? progress.Report : null, ct);
            _stagedMgr = mgr;
            _staged = info;

            // Automatically stage it to apply silently on exit without forcing a restart
            try
            {
                mgr.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false);
            }
            catch { }

            UpdateReady?.Invoke();
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Check for, download, and stage an update in the background.</summary>
    public async Task CheckAndStageAsync()
    {
        await CheckAndAutoUpdateAsync();
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
                if (CustomDownloadUpdatesHandler is not null)
                    await CustomDownloadUpdatesHandler(targetVer);
                UpdateReady?.Invoke();
                return false;
            }
        }

        // 2. Production Velopack workflow
        var mgr = GetManager();
        if (mgr is null || !mgr.IsInstalled) return false;

        try
        {
            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) return false;

            string targetVersion = info.TargetFullRelease.Version.ToString();

            bool updateNow = PromptUserForUpdate is not null
                ? await PromptUserForUpdate(targetVersion)
                : await DefaultPromptAsync(targetVersion);

            await mgr.DownloadUpdatesAsync(info);
            _stagedMgr = mgr;
            _staged = info;

            if (updateNow)
            {
                UpdateReady?.Invoke();
                ApplyAndRestart();
                return true;
            }
            else
            {
                try
                {
                    mgr.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false);
                }
                catch { }
                UpdateReady?.Invoke();
                return false;
            }
        }
        catch
        {
            return false;
        }
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
        if (_stagedMgr is not null && _staged is not null)
            _stagedMgr.ApplyUpdatesAndRestart(_staged.TargetFullRelease, restartArgs);
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
        }
    }

    public void Dispose()
    {
        StopPeriodicChecks();
    }
}
