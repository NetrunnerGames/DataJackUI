using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using DataJackUIGui.Models;
using DataJackUIGui.Services.Downloads;
using Microsoft.Extensions.Logging;

namespace DataJackUIGui.Services;

/// <summary>The outcome of making SteamAutoCrack runnable, so callers can say what actually happened.</summary>
public enum SacPrepareResult
{
    Ready,
    /// <summary>The user declined the .NET runtime installer's elevation prompt. Not an error.</summary>
    RuntimeDeclined,
    /// <summary>The runtime installed but Windows wants a reboot before it can be used.</summary>
    RuntimeNeedsRestart,
    RuntimeFailed,
}

/// <summary>
/// Downloads and launches SteamAutoCrack (SteamAutoCracks/Steam-auto-crack).
/// </summary>
/// <remarks>
/// <para>We can only <b>open</b> it. The published release contains a single GUI exe with no
/// SteamAutoCrack.CLI.exe, and that GUI parses no command-line arguments, so there is no way to hand it
/// an appid or a path. The user does everything inside their own window.</para>
///
/// <para>Their exe is a FRAMEWORK-DEPENDENT net10.0-windows single-file build: the bundle carries no
/// hostpolicy/coreclr/System.Private.CoreLib and declares Microsoft.WindowsDesktop.App, so it cannot
/// start without the .NET 10 <i>Desktop</i> runtime. <see cref="EnsureRuntimeAsync"/> installs that on
/// demand through Velopack's Runtimes API, which we already depend on for updates.</para>
///
/// <para>Update handling mirrors <see cref="DepotDownloaderService"/> and <see cref="SteamlessService"/>:
/// the installed release tag is recorded, re-checked at most every <see cref="ToolCheckInterval"/>, the
/// asset digest is verified before extracting, and <b>every failure path falls back to an existing exe
/// while still recording the attempt</b> so a failing check is not retried on the next click.</para>
/// </remarks>
public class SteamAutoCrackService(
    GithubProxy gh,
    CacheService cache,
    ILogger<SteamAutoCrackService> log,
    SettingsService? settings = null)
{
    private static readonly string ToolDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUIGui", "steamautocrack");

    private string ExePath
    {
        get
        {
            if (!string.IsNullOrEmpty(settings?.ToolsPath) && Directory.Exists(settings.ToolsPath))
            {
                string custom = Path.Combine(settings.ToolsPath, "SteamAutoCrack.exe");
                if (File.Exists(custom)) return custom;
                string customSub = Path.Combine(settings.ToolsPath, "SteamAutoCrack", "SteamAutoCrack.exe");
                if (File.Exists(customSub)) return customSub;
            }

            string localAppTools = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "SteamAutoCrack.exe");
            if (File.Exists(localAppTools)) return localAppTools;
            string localAppToolsSub = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "SteamAutoCrack", "SteamAutoCrack.exe");
            if (File.Exists(localAppToolsSub)) return localAppToolsSub;

            return Path.Combine(ToolDir, "SteamAutoCrack.exe");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>How long an up-to-date check is trusted before we ask GitHub again.</summary>
    private static readonly TimeSpan ToolCheckInterval = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private static bool CheckedRecently(long lastMs) =>
        lastMs > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastMs < (long)ToolCheckInterval.TotalMilliseconds;

    // ── Runtime ──────────────────────────────────────────────────────

    /// <summary>
    /// The .NET 10 Desktop runtime their exe needs, named for this machine's architecture.
    /// </summary>
    /// <remarks>
    /// Velopack's static fields stop at DOTNET8, but GetRuntimeByName parses the id generically, so
    /// "net10-x64-desktop" resolves fine on 1.2.0 without an upgrade. Their asset carries no RID in its
    /// name, so the OS architecture is the honest guess at which runtime it wants.
    /// </remarks>
    private static string RuntimeArchitecture => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        _ => "x64",
    };

    /// <summary>
    /// Checks if Microsoft.WindowsDesktop.App 10.x is installed locally.
    /// </summary>
    public async Task<bool> RuntimeInstalledAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                string baseDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                if (Directory.Exists(baseDotnet))
                {
                    var dirs = Directory.GetDirectories(baseDotnet, "10.*");
                    if (dirs.Length > 0) return true;
                }

                var psi = new ProcessStartInfo("dotnet", "--list-runtimes")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p is not null)
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    if (output.Contains("Microsoft.WindowsDesktop.App 10.", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Checking for the .NET runtime failed");
            }
            return false;
        });
    }

    /// <summary>
    /// Make sure the .NET 10 Desktop runtime is present, downloading and installing it if not.
    /// </summary>
    public async Task<SacPrepareResult> EnsureRuntimeAsync(
        IProgress<double?>? progress, CancellationToken ct = default)
    {
        try
        {
            if (await RuntimeInstalledAsync()) return SacPrepareResult.Ready;

            Directory.CreateDirectory(ToolDir);
            string arch = RuntimeArchitecture;
            string installer = Path.Combine(ToolDir, $"windowsdesktop-runtime-10.0-{arch}.exe");

            string downloadUrl = $"https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-{arch}.exe";
            ct.ThrowIfCancellationRequested();

            using (var client = new System.Net.Http.HttpClient())
            using (var response = await client.GetAsync(downloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 60_000_000;
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = File.Create(installer);
                byte[] buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    progress?.Report((double)read / total);
                }
            }

            ct.ThrowIfCancellationRequested();
            var installPsi = new ProcessStartInfo(installer, "/install /quiet /norestart")
            {
                UseShellExecute = true
            };
            using var proc = Process.Start(installPsi);
            if (proc is null) return SacPrepareResult.RuntimeFailed;
            await proc.WaitForExitAsync(ct);

            try { File.Delete(installer); } catch { }

            if (proc.ExitCode == 0) return SacPrepareResult.Ready;
            if (proc.ExitCode == 1602 || proc.ExitCode == 1223) return SacPrepareResult.RuntimeDeclined;
            if (proc.ExitCode == 3010) return SacPrepareResult.RuntimeNeedsRestart;

            return SacPrepareResult.RuntimeFailed;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Installing the .NET runtime for SteamAutoCrack failed");
            return SacPrepareResult.RuntimeFailed;
        }
    }

    // ── Tool ─────────────────────────────────────────────────────────

    /// <summary>
    /// Ensure SteamAutoCrack is on disk and reasonably current. Null only if no usable copy exists.
    /// </summary>
    /// <param name="force">
    /// Skip the throttle. Load-bearing for the background-update path: <see cref="IsUpdateAvailableAsync"/>
    /// records the check timestamp, so without this the job it queues would see "checked recently" and
    /// skip the very download it was queued to perform.
    /// </param>
    public async Task<string?> EnsureToolAsync(
        IProgress<DownloadProgress>? progress, bool force = false, CancellationToken ct = default)
    {
        if (!force && File.Exists(ExePath) && CheckedRecently(cache.SteamAutoCrackCheckedAtMs)) return ExePath;

        await _gate.WaitAsync(ct);
        bool have = false;
        try
        {
            have = File.Exists(ExePath);
            if (!force && have && CheckedRecently(cache.SteamAutoCrackCheckedAtMs)) return ExePath;

            var (release, asset) = await FetchLatestReleaseAsync(ct);
            if (asset is null)
            {
                if (have) RecordAttempt();
                return have ? ExePath : null;
            }

            if (!force && have && !string.IsNullOrEmpty(release!.TagName) &&
                string.Equals(release.TagName, cache.SteamAutoCrackVersion, StringComparison.Ordinal))
            {
                RecordAttempt();
                return ExePath;
            }

            if (!await DownloadAndExtractToolAsync(asset, progress, ct))
            {
                if (have) RecordAttempt();
                return have ? ExePath : null;
            }

            cache.SteamAutoCrackVersion = release!.TagName;
            RecordAttempt();
            return ExePath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Obtaining SteamAutoCrack failed");
            if (have) RecordAttempt();
            return have ? ExePath : null;
        }
        finally { _gate.Release(); }
    }

    private void RecordAttempt() =>
        cache.SteamAutoCrackCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private async Task<(GithubRelease? release, GithubAsset? asset)> FetchLatestReleaseAsync(CancellationToken ct)
    {
        string url = $"https://api.github.com/repos/{AppConfig.SteamAutoCrackRepo}/releases/latest";
        using var res = await gh.SendAsync(url, ct);
        if (res is null || !res.IsSuccessStatusCode)
        {
            log.LogDebug("SteamAutoCrack release lookup failed: {Status}", res?.StatusCode);
            return (null, null);
        }

        var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
        var asset = release?.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (asset is null) log.LogDebug("SteamAutoCrack release has no .zip asset");
        return (release, asset);
    }

    private async Task<bool> DownloadAndExtractToolAsync(GithubAsset asset, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolDir);
        string zipPath = Path.Combine(ToolDir, "steamautocrack.zip");
        var sink = progress is null ? null : new ProgressRelay<double?>(f =>
            progress.Report(new DownloadProgress((long)((f ?? 0) * asset.Size), asset.Size > 0 ? asset.Size : null)));
        
        await gh.DownloadAsync(asset.DownloadUrl, zipPath, sink, ct);

        if (!AssetHash.Matches(zipPath, asset.Digest))
        {
            log.LogDebug("SteamAutoCrack asset digest mismatch; keeping the existing copy");
            try { File.Delete(zipPath); } catch { }
            return false;
        }

        ZipFile.ExtractToDirectory(zipPath, ToolDir, overwriteFiles: true);
        try { File.Delete(zipPath); } catch { }

        return File.Exists(ExePath);
    }

    /// <summary>
    /// Open it immediately when nothing needs downloading. False means the caller should run the full
    /// job (which installs the runtime and/or the tool).
    /// </summary>
    /// <remarks>
    /// This exists so a launch that transfers zero bytes never touches the queue: a queued job would
    /// flash a progress row and, worse, leave a permanent entry in the user's download history next to
    /// their real game downloads.
    ///
    /// The runtime check is not redundant with <c>File.Exists</c>. A framework-dependent exe with no
    /// runtime still STARTS, and then shows Windows' own "you must install .NET" dialog - so skipping it
    /// would hand the user that dialog instead of our install flow.
    /// </remarks>
    public async Task<bool> TryLaunchIfReadyAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ExePath)) return false;
        if (!await RuntimeInstalledAsync()) return false;
        return Launch();
    }

    /// <summary>
    /// Throttled "is there a newer build?" probe. No download, and never blocks a launch.
    /// </summary>
    /// <remarks>
    /// Returns false when already current, when the 6h window has not elapsed, and on any failure -
    /// recording the attempt each time so an offline machine does not re-walk the mirror chain on every
    /// click. A true result is expected to be followed by <c>EnsureToolAsync(force: true)</c>.
    /// </remarks>
    public async Task<bool> IsUpdateAvailableAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ExePath)) return false;
        if (CheckedRecently(cache.SteamAutoCrackCheckedAtMs)) return false;

        await _gate.WaitAsync(ct);
        try
        {
            if (CheckedRecently(cache.SteamAutoCrackCheckedAtMs)) return false; // won the race

            void RecordAttempt() =>
                cache.SteamAutoCrackCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string url = $"https://api.github.com/repos/{AppConfig.SteamAutoCrackRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) { RecordAttempt(); return false; }

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            RecordAttempt();

            return !string.IsNullOrEmpty(release?.TagName)
                && !string.Equals(release!.TagName, cache.SteamAutoCrackVersion, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Checking for a SteamAutoCrack update failed");
            cache.SteamAutoCrackCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Open SteamAutoCrack's window. Fire-and-forget; we don't wait for it to exit.</summary>
    public bool Launch()
    {
        if (!File.Exists(ExePath)) return false;
        try
        {
            // WorkingDirectory matters: their exe looks for Goldberg/ and TEMP/ next to itself.
            Process.Start(new ProcessStartInfo(ExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = ToolDir,
            });
            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Launching SteamAutoCrack failed");
            return false;
        }
    }
}
