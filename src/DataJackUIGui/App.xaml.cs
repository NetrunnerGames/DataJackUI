using System.Windows;
using System.Windows.Threading;
using DataJackUIGui.Models;
using DataJackUIGui.Services;
using DataJackUIGui.ViewModels;
using DataJackUIGui.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DataJackUIGui;

public partial class App : Application
{
    private readonly IHost _host;

    // True when the app was cold-started solely to run a silent install AND MinimizeToTray is off,
    // which means we auto-exit after the balloon so we don't leave a ghost tray icon behind.
    private bool _exitAfterSilentInstall;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<SettingsService>();
                services.AddSingleton<CacheService>();
                services.AddSingleton<SteamService>();
                services.AddSingleton<SteamAppListCache>();
                services.AddSingleton<SteamAppInfoCache>();
                services.AddSingleton<CoverCache>();
                services.AddSingleton<ToastService>();
                services.AddSingleton<SteamDepotInfo>();
                services.AddSingleton<LuaVault>();
                services.AddSingleton<Services.AppInfo.LaunchModStore>();
                services.AddSingleton<Services.AppInfo.LaunchOptionsService>();
                services.AddSingleton<LuaInstaller>();
                services.AddSingleton<SteamLibraryService>();
                services.AddSingleton<DonateKeysService>();
                services.AddSingleton<AnalyticsService>();
                services.AddSingleton<GithubProxy>();
                services.AddSingleton<HardwareAppIdService>();
                services.AddSingleton<SteamlessService>();
                services.AddSingleton<SteamAutoCrackService>();
                services.AddSingleton<CloudRedirectService>();
                services.AddSingleton<DepotDownloaderService>();
                services.AddSingleton<DepotCacheMigrationService>();
                services.AddSingleton<AppliedFixIndexService>();
                services.AddSingleton<UnlockerService>();
                services.AddSingleton<PluginInstallerService>();
                services.AddTransient<DropInstallViewModel>(); // one per page (Home, Add)
                services.AddSingleton<AuthService>();
                services.AddSingleton<DataJackUIApiClient>();
                services.AddSingleton<HubcapService>();
                services.AddSingleton<UpdateService>();
                // Central download queue. Singleton + hosted service (same pattern as HttpServerService
                // below): the hosted lifetime runs the scheduler pump, and view models resolve the same
                // instance to enqueue and observe.
                services.AddSingleton<Services.Downloads.DownloadQueue>();
                services.AddHostedService(sp => sp.GetRequiredService<Services.Downloads.DownloadQueue>());
                services.AddSingleton<Services.Downloads.ManifestJobFactory>();
                // Hook loader infrastructure
                services.AddSingleton<PluginAddService>();
                services.AddSingleton<HttpServerService>();
                services.AddHostedService(sp => sp.GetRequiredService<HttpServerService>());
                // Also resolvable as a plain singleton (not just IHostedService) so PluginInstallerService
                // can call ReloadPluginFilesAsync() after install/uninstall. Same pattern as HttpServerService.
                services.AddSingleton<CefInjectorService>();
                services.AddHostedService(sp => sp.GetRequiredService<CefInjectorService>());
                services.AddSingleton<DownloadViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<ManageViewModel>();
                services.AddSingleton<BuildsViewModel>();
                services.AddTransient<LaunchOptionsViewModel>(); // one per dialog
                services.AddSingleton<HomeViewModel>();
                services.AddSingleton<ModeViewModel>();
                services.AddSingleton<FixesViewModel>();
                services.AddSingleton<DownloadsViewModel>();
                services.AddSingleton<PluginViewModel>();
                services.AddSingleton<OnboardingViewModel>();
                services.AddSingleton<MainViewModel>();
                // Pages resolved by NavigationView via the DI service provider.
                services.AddSingleton<HomeView>();
                services.AddSingleton<DownloadView>();
                services.AddSingleton<DownloadsView>();
                services.AddSingleton<ManageView>();
                services.AddSingleton<BuildsView>();
                services.AddSingleton<ModeView>();
                services.AddSingleton<FixesView>();
                services.AddSingleton<PluginView>();
                services.AddSingleton<SettingsView>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    private UpdateService Updates => _host.Services.GetRequiredService<UpdateService>();

    // Guards RunUpdateFlowAsync so overlapping triggers (startup + the re-poke a DLL/Steam restart causes)
    // never run it concurrently. A second caller drops out immediately.
    private readonly System.Threading.SemaphoreSlim _updateFlowGate = new(1, 1);

    /// <summary>
    /// Warn when Steam has overwritten launch options we'd applied, and offer to put them back.
    ///
    /// <para>
    /// Steam rebuilds appinfo.vdf from PICS on login, app updates and store browsing. It did so twice
    /// while this feature was being written, so an applied edit is not permanent. Re-applying is offered
    /// but never automatic: it closes Steam, which is not something to do behind the user's back at
    /// startup. Costs nothing when no launch options have been edited (the store short-circuits on empty).
    /// </para>
    /// </summary>
    private async Task CheckLaunchOptionDriftAsync()
    {
        try
        {
            var launch = _host.Services.GetRequiredService<Services.AppInfo.LaunchOptionsService>();
            if (launch.Store.IsEmpty) return;

            // Indexing the ~373 MB cache takes a couple of seconds, never on the UI thread.
            var drifted = await Task.Run(launch.FindDrifted);
            if (drifted.Count == 0) return;

            var toast = _host.Services.GetRequiredService<ToastService>();
            Dispatcher.Invoke(() => toast.ShowAction(
                DataJackUIGui.Resources.Strings.Launch_Drift_Title,
                string.Format(DataJackUIGui.Resources.Strings.Launch_Drift_Body, drifted.Count),
                DataJackUIGui.Resources.Strings.Launch_Drift_Action,
                () => _ = ReapplyDriftedAsync(launch, drifted, toast)));
        }
        catch
        {
            // Cache locked/unreadable: nothing actionable, and this must never block startup.
        }
    }

    /// <summary>
    /// The drift notice's "Re-apply" button: confirm, then write the staged edits back into appinfo.
    ///
    /// <para>
    /// The write runs OFF the UI thread. Unlike the launch-options dialog (which is modal, so its own
    /// synchronous apply merely blocks a window that's already blocking), this fires with the main window
    /// live, and <c>Apply</c> indexes a ~373 MB file, copies a backup and rewrites it. On the UI thread
    /// that's a multi-second freeze of the whole app.
    /// </para>
    /// </summary>
    private static async Task ReapplyDriftedAsync(
        Services.AppInfo.LaunchOptionsService launch, IReadOnlyList<int> drifted, ToastService toast)
    {
        // Same wording as the dialog's own prompt: closing Steam should never read as a different
        // decision depending on where it was triggered from.
        if (MessageBox.Show(
                DataJackUIGui.Resources.Strings.Launch_ApplyNow_Body,
                DataJackUIGui.Resources.Strings.Launch_ApplyNow_Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var result = await Task.Run(() => launch.Reapply(drifted));

        if (result.Ok)
            toast.Show(DataJackUIGui.Resources.Strings.Launch_Title,
                result.SteamWasRunning
                    ? DataJackUIGui.Resources.Strings.Launch_Applied_Restarted
                    : DataJackUIGui.Resources.Strings.Launch_Applied);
        else
            toast.Show(DataJackUIGui.Resources.Strings.Launch_Title,
                string.Format(DataJackUIGui.Resources.Strings.Launch_ApplyFailed, result.Error), error: true);
    }

    /// <summary>Set by OnStartup to <see cref="RunUpdateFlowAsync"/> so non-UI callers (e.g. the
    /// /check-updates HTTP handler) can run the exact same update flow instead of a divergent one.</summary>
    internal static Func<Task>? RunUpdateFlow;

    /// <summary>The Steam-open update flow (fully silent): update the APP first, unconditionally, before
    /// ever touching the plugin. Then, once the running app is guaranteed current, check/apply a plugin
    /// update against it. Called on a loader (--tray-locked) launch and on the Steam-open re-check poke;
    /// safe to call repeatedly.
    /// <para>
    /// App-before-plugin is load-bearing, not just tidy ordering: the app and plugin are NOT independently
    /// safe to update out of order whenever a plugin release changes something the app's own compiled code
    /// depends on (e.g. <see cref="Services.CefInjectorService"/>'s CDP port is a compile-time constant.
    /// An old app build talking to a freshly-updated plugin that moved the port simply can't connect, and
    /// won't self-heal until the app itself happens to update, which is not guaranteed to land in the same
    /// pass: the app and plugin ship from separate repos on separate cadences, so one can succeed while the
    /// other fails/lags). Restarting into the latest app FIRST, before it goes anywhere near a plugin
    /// update, means whatever the plugin changes is always applied by a process that already understands
    /// it.
    /// </para></summary>
    private async Task RunUpdateFlowAsync()
    {
        if (!_updateFlowGate.Wait(0)) return; // another run already in progress
        try
        {
            // 1) Stage + immediately apply any app update, before touching the plugin at all.
            //    ApplyAndRestart() terminates this process; the relaunched instance (launched with
            //    --tray-locked) re-enters this same flow via OnStartup once it's already current, so this
            //    run's job ends here. There is nothing safe left for THIS process to do.
            try { await Updates.CheckAndStageAsync(); } catch { /* offline / not installed */ }
            if (Updates.HasStagedUpdate)
            {
                Dispatcher.Invoke(() => Updates.ApplyAndRestart(new[] { "--minimized", "--tray-locked" }));
                return;
            }

            // 2) No app update pending: safe to check/apply a plugin update against this (already-current) app.
            try
            {
                var installer = _host.Services.GetRequiredService<PluginInstallerService>();
                var st = await installer.GetStatusAsync(force: true);
                if (st.UpdateAvailable)
                {
                    if (!st.DllMatches)
                    {
                        var t = _host.Services.GetRequiredService<ToastService>();
                        Dispatcher.Invoke(() => t.Show("DataJackUI", "Updating plugin. Steam will restart."));
                    }
                    await installer.InstallAsync(progress: null);
                }
            }
            catch { /* offline / install error. Retry next Steam-open */ }
        }
        finally { _updateFlowGate.Release(); }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        RunStartupCleanup();
        await _host.StartAsync();

        if (ModeMigration.Apply(_host.Services.GetRequiredService<SettingsService>()))
            _host.Services.GetRequiredService<CacheService>().OnboardingComplete = false;

        _host.Services.GetRequiredService<UnlockerService>().EnsureLuaPathRegistered();

        var main = _host.Services.GetRequiredService<MainViewModel>();
        var settingsVm = _host.Services.GetRequiredService<SettingsViewModel>();
        var window = _host.Services.GetRequiredService<MainWindow>();

        WireSettingsAndWindow(settingsVm, main, window);
        WireGlobalEvents(window);
        WireNavigation(window);
        WireLibraryRefresh(window);

        string? url = Program.StartupUrl ?? ProtocolService.TryReadPending();
        bool silentStartup = (url is not null && ProtocolService.Parse(url).Silent) || Program.StartMinimized;
        _exitAfterSilentInstall = silentStartup && Program.StartupUrl is not null && !settingsVm.MinimizeToTray;

        await ShowOrInitializeSilentAsync(silentStartup, window, main);

        if (url is not null) HandleProtocolUrl(url);

        StartBackgroundTasks();
    }

    private void RunStartupCleanup()
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string legacy = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DataJackUI");
                if (System.IO.Directory.Exists(legacy)) System.IO.Directory.Delete(legacy, recursive: true);
            }
            catch { }
            Services.Downloads.HttpFileDownloader.SweepStale();
        });
    }

    private void WireSettingsAndWindow(SettingsViewModel settingsVm, MainViewModel main, MainWindow window)
    {
        settingsVm.RequestRestart = RelaunchApp;
        settingsVm.RequestShowWindow = () => Dispatcher.Invoke(window.RestoreFromTray);
        settingsVm.RequestSignIn = () => main.SignInCommand.ExecuteAsync(null);

        Func<Task> navigateToSignIn = () =>
        {
            Dispatcher.Invoke(() =>
            {
                settingsVm.LoginRequiredMessage = DataJackUIGui.Resources.Strings.Settings_LoginRequired;
                window.NavigateToSettings();
            });
            return Task.CompletedTask;
        };
        _host.Services.GetRequiredService<DownloadViewModel>().RequestSignIn = navigateToSignIn;
        _host.Services.GetRequiredService<FixesViewModel>().RequestSignIn = navigateToSignIn;

        var toast = _host.Services.GetRequiredService<ToastService>();
        toast.Attach(window.RootSnackbar);

        settingsVm.RequestRestartPrompt = () => Dispatcher.Invoke(() =>
            toast.ShowAction(DataJackUIGui.Resources.Strings.Lang_Changed_Title, DataJackUIGui.Resources.Strings.Lang_Changed_Body,
                DataJackUIGui.Resources.Strings.Lang_Changed_Restart, () => settingsVm.RequestRestart?.Invoke()));
    }

    private void WireGlobalEvents(MainWindow window)
    {
        if (Program.ShowWindowSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(Program.ShowWindowSignal, (_, _) => Dispatcher.Invoke(() =>
            {
                string? pending = ProtocolService.TryReadPending();
                if (pending is null || !ProtocolService.Parse(pending).Silent) window.RestoreFromTray();
                if (pending is not null) HandleProtocolUrl(pending);
            }), null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);

        if (Program.EnableTrayLockSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(Program.EnableTrayLockSignal, (_, _) => Program.SessionTrayLock = true, null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);

        if (Program.RecheckUpdatesSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(Program.RecheckUpdatesSignal, (_, _) => _ = RunUpdateFlowAsync(), null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);

        RunUpdateFlow = RunUpdateFlowAsync;
        _ = CheckLaunchOptionDriftAsync();
    }

    private void WireNavigation(MainWindow window)
    {
        var download = _host.Services.GetRequiredService<DownloadViewModel>();
        var manage = _host.Services.GetRequiredService<ManageViewModel>();
        var builds = _host.Services.GetRequiredService<BuildsViewModel>();
        var home = _host.Services.GetRequiredService<HomeViewModel>();
        var main = _host.Services.GetRequiredService<MainViewModel>();

        manage.NavigateToAdd = appId => Dispatcher.Invoke(() => { window.NavigateToAdd(); download.SeedSearch(appId); });
        manage.NavigateToBuilds = appId => Dispatcher.Invoke(() => { window.NavigateToBuilds(); _ = builds.SelectAppAsync(appId); });
        manage.OpenLaunchOptions = (appId, name) => Dispatcher.Invoke(() =>
        {
            var dialog = new LaunchOptionsDialog(_host.Services.GetRequiredService<LaunchOptionsViewModel>(), appId, name) { Owner = window };
            dialog.ShowDialog();
        });

        Action<long> openInManage = appId => Dispatcher.Invoke(() => { window.NavigateToManage(); _ = manage.OpenDetailForAppIdAsync(appId); });
        home.NavigateToGame = openInManage;
        download.NavigateToGame = openInManage;
        builds.NavigateToManage = openInManage;

        _host.Services.GetRequiredService<DownloadsViewModel>().RevealItem = _ => window.NavigateToAdd();
        builds.RequestShowDownloads = () => Dispatcher.Invoke(window.NavigateToDownloads);

        Func<long, Task> installByAppId = appId => { Dispatcher.Invoke(() => HandleProtocolUrl($"datajackui://install/{appId}")); return Task.CompletedTask; };
        home.Drop.InstallByAppId = installByAppId;
        download.Drop.InstallByAppId = installByAppId;

        home.NavigateToPlugin = () => Dispatcher.Invoke(window.NavigateToPlugin);
        home.NavigateToManage = () => Dispatcher.Invoke(window.NavigateToManage);
        home.NavigateToSettings = () => Dispatcher.Invoke(window.NavigateToSettings);
        home.NavigateToMode = () => Dispatcher.Invoke(window.NavigateToMode);
        main.Onboarding.RefreshHome = () => Dispatcher.Invoke(() => home.LoadAsync());
    }

    private void WireLibraryRefresh(MainWindow window)
    {
        var luaInstaller = _host.Services.GetRequiredService<LuaInstaller>();
        var appInfo = _host.Services.GetRequiredService<SteamAppInfoCache>();
        var manage = _host.Services.GetRequiredService<ManageViewModel>();
        var builds = _host.Services.GetRequiredService<BuildsViewModel>();
        var home = _host.Services.GetRequiredService<HomeViewModel>();

        luaInstaller.Installed += appId => Dispatcher.InvokeAsync(async () =>
        {
            _ = manage.LoadAsync();
            _ = builds.LoadAsync();
            await home.RefreshLibraryAsync();
            if (await appInfo.EnsureFullDetailsAsync(appId)) await home.RefreshLibraryAsync();
        });
    }

    private async Task ShowOrInitializeSilentAsync(bool silentStartup, MainWindow window, MainViewModel main)
    {
        if (silentStartup)
        {
            window.StartSilent();
            try { await main.InitializeAsync(); } catch { }
        }
        else
        {
            window.Show();
            var cache = _host.Services.GetRequiredService<CacheService>();
            var auth = _host.Services.GetRequiredService<AuthService>();

            if (!auth.IsSignedIn)
            {
                main.Onboarding.IsOpen = true;
            }
            else if (!cache.OnboardingComplete)
            {
                var unlocker = _host.Services.GetRequiredService<UnlockerService>();
                var installer = _host.Services.GetRequiredService<PluginInstallerService>();
                bool configured = unlocker.SelectedMode is (UnlockerMode.Ost or UnlockerMode.IceBreaker) && installer.IsInstalledLocally();
                if (configured) cache.OnboardingComplete = true;
                else main.Onboarding.IsOpen = true;
            }
        }
    }

    private void StartBackgroundTasks()
    {
        if (Program.SessionTrayLock) _ = RunUpdateFlowAsync();
        _ = _host.Services.GetRequiredService<DonateKeysService>().SendPendingKeysIfEnabledAsync();
        _ = _host.Services.GetRequiredService<AnalyticsService>().TrackAppLaunchAsync();
        _ = _host.Services.GetRequiredService<HardwareAppIdService>().EnsureFreshAsync();
        _ = _host.Services.GetRequiredService<DepotCacheMigrationService>().RunAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // If an update was downloaded but not yet applied, stage it for after exit.
        if (Updates.HasStagedUpdate)
            Updates.ApplyOnExit();

        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }

    /// <summary>Relaunch the app (used after a language change). The single-instance mutex is released
    /// only when THIS process exits, so we start the new instance via a short delayed shell command. By
    /// the time it launches the exe, our mutex is free and the new instance won't bow out.</summary>
    private void RelaunchApp()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
            {
                // cmd: wait ~1.2s for this process's mutex to release, then start the exe detached.
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch { /* if relaunch fails, the user can reopen manually */ }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>Route a datajackui:// protocol URL to the appropriate page and action.</summary>
    private void HandleProtocolUrl(string url)
    {
        var (action, appId, silent, code) = ProtocolService.Parse(url);
        if (action is null) return;

        var window = _host.Services.GetRequiredService<MainWindow>();

        switch (action)
        {
            case "oauth":
            case "auth":
                if (!string.IsNullOrEmpty(code))
                    _ = _host.Services.GetRequiredService<AuthService>().ExchangeCodeForSessionAsync(code);
                break;
            case "game":
                if (appId.HasValue) HandleGameProtocol(appId.Value, window);
                break;
            case "install":
                if (appId.HasValue) HandleInstallProtocol(appId.Value, silent, window);
                break;
            case "manage":
                if (appId.HasValue) HandleManageProtocol(appId.Value, window);
                break;
            case "fix":
                if (appId.HasValue) HandleFixProtocol(appId.Value, window);
                break;
        }
    }

    private void HandleGameProtocol(long appId, MainWindow window)
    {
        window.NavigateToAdd();
        _host.Services.GetRequiredService<DownloadViewModel>().SeedSearch(appId);
    }

    private void HandleInstallProtocol(long appId, bool silent, MainWindow window)
    {
        var download = _host.Services.GetRequiredService<DownloadViewModel>();
        if (silent)
        {
            _ = download.ProtocolInstall(appId, (msg, error) => Dispatcher.Invoke(() =>
            {
                window.ShowInstallNotification(msg, error);
                var queue = _host.Services.GetRequiredService<Services.Downloads.DownloadQueue>();
                if (_exitAfterSilentInstall && queue.ActiveCount == 0)
                    _ = Task.Delay(6000).ContinueWith(_ => Dispatcher.Invoke(Shutdown));
            }));
        }
        else
        {
            window.NavigateToAdd();
            _ = download.ProtocolInstall(appId);
        }
    }

    private void HandleManageProtocol(long appId, MainWindow window)
    {
        window.NavigateToManage();
        _ = _host.Services.GetRequiredService<ManageViewModel>().OpenDetailForAppIdAsync(appId);
    }

    private void HandleFixProtocol(long appId, MainWindow window)
    {
        window.NavigateToFixes();
        _ = _host.Services.GetRequiredService<FixesViewModel>().OpenForAppIdAsync(appId);
    }
}
