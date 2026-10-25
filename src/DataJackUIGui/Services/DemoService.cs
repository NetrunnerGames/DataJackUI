using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataJackUIGui.Services;

public record DemoGameEntry(
    long AppId,
    string GameTitle,
    string? ExePath,
    DateTime CreatedAt,
    int TimeLimitMinutes = 10,
    bool Expired = false
);

public class DemoService
{
    private static readonly string DemoFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DataJackUIGui",
        "demo_games.json");

    private readonly SteamService _steam;
    private readonly SteamLibraryService _library;
    private readonly SteamDepotInfo _depots;
    private readonly ToastService _toast;
    private readonly IceBreakerIpcClient _ipc;

    private readonly ConcurrentDictionary<long, DemoGameEntry> _activeDemos = new();
    private readonly ConcurrentDictionary<long, DateTime> _startedTimes = new();
    private readonly ConcurrentDictionary<long, bool> _notifiedOneMinuteWarning = new();
    private CancellationTokenSource? _watchCts;

    public DemoService(SteamService steam, SteamLibraryService library, SteamDepotInfo depots, ToastService toast, IceBreakerIpcClient ipc)
    {
        _steam = steam;
        _library = library;
        _depots = depots;
        _toast = toast;
        _ipc = ipc;

        LoadDemos();
        StartWatcher();
    }

    public bool IsDemoApp(long appId) => _activeDemos.ContainsKey(appId);

    public void RegisterDemo(long appId, string gameTitle)
    {
        var entry = new DemoGameEntry(appId, gameTitle, null, DateTime.UtcNow, 10, false);
        _activeDemos[appId] = entry;
        SaveDemos();
        _ = ResolveAndCacheExesAsync(appId);
    }

    private void LoadDemos()
    {
        try
        {
            if (File.Exists(DemoFilePath))
            {
                var json = File.ReadAllText(DemoFilePath);
                var list = JsonSerializer.Deserialize<List<DemoGameEntry>>(json);
                if (list is not null)
                {
                    foreach (var item in list)
                    {
                        if (!item.Expired) _activeDemos[item.AppId] = item;
                    }
                }
            }
        }
        catch { }
    }

    private void SaveDemos()
    {
        try
        {
            string? dir = Path.GetDirectoryName(DemoFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(DemoFilePath, JsonSerializer.Serialize(_activeDemos.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private async Task ResolveAndCacheExesAsync(long appId)
    {
        try
        {
            string? installDir = _library.GetInstallDir(appId);
            if (installDir is null) return;

            var depotInfo = await _depots.GetAsync(appId);
            string? targetExe = null;

            if (depotInfo is { LaunchExes.Count: > 0 })
            {
                targetExe = depotInfo.LaunchExes
                    .Select(rel => Path.Combine(installDir, rel))
                    .FirstOrDefault(File.Exists);
            }

            if (targetExe is null && Directory.Exists(installDir))
            {
                targetExe = Directory.EnumerateFiles(installDir, "*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(p => !p.Contains("redist", StringComparison.OrdinalIgnoreCase) &&
                                         !p.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                                         !p.Contains("crash", StringComparison.OrdinalIgnoreCase) &&
                                         !p.Contains("unins", StringComparison.OrdinalIgnoreCase));
            }

            if (targetExe is not null && _activeDemos.TryGetValue(appId, out var entry))
            {
                _activeDemos[appId] = entry with { ExePath = targetExe };
                SaveDemos();
            }
        }
        catch { }
    }

    private void StartWatcher()
    {
        _watchCts?.Cancel();
        var cts = _watchCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, cts.Token);
                    await CheckRunningDemosAsync();
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, cts.Token);
    }

    private async Task CheckRunningDemosAsync()
    {
        if (_activeDemos.IsEmpty) return;

        var procs = Process.GetProcesses();

        foreach (var (appId, demo) in _activeDemos)
        {
            string? exeName = null;
            if (!string.IsNullOrEmpty(demo.ExePath))
            {
                exeName = Path.GetFileNameWithoutExtension(demo.ExePath);
            }

            // If we don't have exe yet, try resolving
            if (string.IsNullOrEmpty(exeName))
            {
                string? installDir = _library.GetInstallDir(appId);
                if (installDir is not null)
                {
                    await ResolveAndCacheExesAsync(appId);
                    if (_activeDemos.TryGetValue(appId, out var updated) && !string.IsNullOrEmpty(updated.ExePath))
                    {
                        exeName = Path.GetFileNameWithoutExtension(updated.ExePath);
                    }
                }
            }

            if (string.IsNullOrEmpty(exeName)) continue;

            var matchingProcs = procs.Where(p => string.Equals(p.ProcessName, exeName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matchingProcs.Count > 0)
            {
                // Game is currently running
                if (!_startedTimes.TryGetValue(appId, out var startTime))
                {
                    startTime = DateTime.UtcNow;
                    _startedTimes[appId] = startTime;
                    _toast.Show("Demo Started", $"You have 10 minutes of demo gameplay for {demo.GameTitle}.", false);
                }

                var elapsed = DateTime.UtcNow - startTime;
                var remaining = TimeSpan.FromMinutes(demo.TimeLimitMinutes) - elapsed;

                if (remaining <= TimeSpan.FromMinutes(1) && remaining > TimeSpan.Zero)
                {
                    if (_notifiedOneMinuteWarning.TryAdd(appId, true))
                    {
                        _toast.Show("Demo Expiring Soon", $"1 minute remaining for {demo.GameTitle} before demo closes and files are removed.", true);
                    }
                }
                else if (remaining <= TimeSpan.Zero)
                {
                    // Force terminate & cleanup!
                    _toast.Show("Demo Expired", $"Time's up! Force closing {demo.GameTitle} and removing files.", true);
                    await ExpireAndCleanupGameAsync(appId, demo, matchingProcs);
                }
            }
        }
    }

    public async Task ExpireAndCleanupGameAsync(long appId, DemoGameEntry demo, List<Process>? runningProcs = null)
    {
        try
        {
            // 1. Terminate running process(es)
            if (runningProcs is not null)
            {
                foreach (var p in runningProcs)
                {
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                }
            }
            else if (!string.IsNullOrEmpty(demo.ExePath))
            {
                string name = Path.GetFileNameWithoutExtension(demo.ExePath);
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                }
            }

            await Task.Delay(1000); // let locks release

            // 2. Delete game install directory in steamapps\common
            string? installDir = _library.GetInstallDir(appId);
            if (installDir is not null && Directory.Exists(installDir))
            {
                try { Directory.Delete(installDir, recursive: true); } catch { }
            }

            // 3. Delete appmanifest_<appid>.acf from all libraries
            string? steamRoot = _steam.EffectivePath;
            if (steamRoot is not null)
            {
                string mainAcf = Path.Combine(steamRoot, "steamapps", $"appmanifest_{appId}.acf");
                if (File.Exists(mainAcf)) { try { File.Delete(mainAcf); } catch { } }

                string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    try
                    {
                        var text = File.ReadAllText(vdf);
                        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"""path""\s*""([^""]+)""");
                        foreach (System.Text.RegularExpressions.Match m in matches)
                        {
                            string libPath = m.Groups[1].Value.Replace(@"\\", @"\");
                            string acf = Path.Combine(libPath, "steamapps", $"appmanifest_{appId}.acf");
                            if (File.Exists(acf)) { try { File.Delete(acf); } catch { } }

                            // Also try delete installDir from other library
                            string otherCommon = Path.Combine(libPath, "steamapps", "common");
                            if (installDir is not null && Directory.Exists(installDir) && installDir.StartsWith(otherCommon, StringComparison.OrdinalIgnoreCase))
                            {
                                try { Directory.Delete(installDir, recursive: true); } catch { }
                            }
                        }
                    }
                    catch { }
                }

                // 4. Delete lua files from config\stplug-in
                string luaPath = Path.Combine(steamRoot, "config", "stplug-in", $"{appId}.lua");
                if (File.Exists(luaPath)) { try { File.Delete(luaPath); } catch { } }

                // 5. Delete manifests from depotcache if any
                string depotCache = Path.Combine(steamRoot, "depotcache");
                if (Directory.Exists(depotCache))
                {
                    try
                    {
                        // Check installed depots or pattern
                        var installedDepots = _library.GetInstalledDepots(appId);
                        foreach (var depotId in installedDepots.Keys)
                        {
                            var files = Directory.GetFiles(depotCache, $"{depotId}_*.manifest");
                            foreach (var f in files) { try { File.Delete(f); } catch { } }
                        }
                    }
                    catch { }
                }

                // 6. Notify IceBreaker to lock and reload
                try
                {
                    await _ipc.LockAppAsync((uint)appId);
                    await _ipc.ReloadManifestsAsync();
                }
                catch { }
            }

            // Remove demo entry
            _activeDemos.TryRemove(appId, out _);
            _startedTimes.TryRemove(appId, out _);
            _notifiedOneMinuteWarning.TryRemove(appId, out _);
            SaveDemos();
        }
        catch { }
    }
}
