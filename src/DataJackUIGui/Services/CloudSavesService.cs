using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataJackUIGui.Services;

public enum SaveLocationType
{
    SteamUserData,
    Documents,
    SavedGames,
    AppDataLocal,
    AppDataRoaming,
    InstallDir,
    Custom
}

public record GameSaveTarget(
    long AppId,
    string GameName,
    string SavePath,
    SaveLocationType LocationType,
    int FileCount,
    long TotalSizeBytes,
    DateTime? LastModified,
    int SnapshotCount,
    DateTime? LastBackupDate)
{
    public bool HasSnapshots => SnapshotCount > 0;
    public string FormattedSize => CloudSavesService.FormatBytes(TotalSizeBytes);
    public string LocationDisplay => LocationType switch
    {
        SaveLocationType.SteamUserData => "Steam UserData",
        SaveLocationType.Documents => "Documents / My Games",
        SaveLocationType.SavedGames => "Saved Games",
        SaveLocationType.AppDataLocal => "Local AppData",
        SaveLocationType.AppDataRoaming => "Roaming AppData",
        SaveLocationType.InstallDir => "Game Directory",
        SaveLocationType.Custom => "Custom Location",
        _ => "Local Storage"
    };
}

public record SaveBackupSnapshot(
    string SnapshotId,
    long AppId,
    string GameName,
    DateTime Timestamp,
    string DirectoryPath,
    string SourcePath,
    int FileCount,
    long TotalSizeBytes,
    string? Note,
    bool IsSafetyBackup)
{
    public string FormattedSize => CloudSavesService.FormatBytes(TotalSizeBytes);
    public string FormattedDate => Timestamp.ToLocalTime().ToString("g");
}

public record CloudSyncResult(bool Success, string Message, int FilesTransferred = 0, string? Output = null);

public partial class CloudSavesService(
    SteamService steam,
    SteamLibraryService steamLibrary,
    SettingsService settings)
{
    private record SnapshotManifest(
        string SnapshotId,
        long AppId,
        string GameName,
        DateTime Timestamp,
        string SourcePath,
        int FileCount,
        long TotalBytes,
        string? Note,
        bool IsSafety);

    /// <summary>Root base directory for storing all save game backups.</summary>
    public string BaseBackupDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUI", "save_backups");

    public string GetAppBackupDir(long appId) =>
        Path.Combine(BaseBackupDir, appId.ToString());

    /// <summary>Format byte counts for human display.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int digitGroups = (int)(Math.Log10(bytes) / Math.Log10(1024));
        digitGroups = Math.Min(digitGroups, units.Length - 1);
        return $"{bytes / Math.Pow(1024, digitGroups):F1} {units[digitGroups]}";
    }

    /// <summary>
    /// Enumerate all games detected on the system and attempt to locate save folders for them.
    /// Combines Steam installed games and Lua-manifest injected games.
    /// </summary>
    public List<GameSaveTarget> EnumerateGameSaves()
    {
        var targets = new Dictionary<long, (string Name, string? InstallDir)>();

        // 1. Installed Steam games
        try
        {
            foreach (var g in steamLibrary.EnumerateInstalled())
            {
                targets[g.AppId] = (g.Name, g.InstallDir);
            }
        }
        catch { }

        // 2. Lua manifests
        try
        {
            foreach (var (appId, _) in LuaInstaller.EnumerateInstalled(steam))
            {
                if (!targets.ContainsKey(appId))
                {
                    string? inst = steamLibrary.GetInstallDir(appId);
                    targets[appId] = (appId.ToString(), inst);
                }
            }
        }
        catch { }

        // 3. Any previously backed-up App IDs that may no longer be installed
        try
        {
            if (Directory.Exists(BaseBackupDir))
            {
                foreach (string dir in Directory.EnumerateDirectories(BaseBackupDir))
                {
                    string name = Path.GetFileName(dir);
                    if (long.TryParse(name, out long appId) && !targets.ContainsKey(appId))
                    {
                        targets[appId] = (appId.ToString(), null);
                    }
                }
            }
        }
        catch { }

        var result = new List<GameSaveTarget>();

        foreach (var (appId, info) in targets)
        {
            var (type, savePath, fileCount, totalBytes, lastMod) = DetectSavePath(appId, info.Name, info.InstallDir);
            var snapshots = GetSnapshots(appId);
            DateTime? lastBackup = snapshots.Count > 0 ? snapshots[0].Timestamp : null;

            // If we have either a detected live save path or existing snapshots, surface the target
            if (savePath != null || snapshots.Count > 0)
            {
                result.Add(new GameSaveTarget(
                    AppId: appId,
                    GameName: info.Name,
                    SavePath: savePath ?? (snapshots.Count > 0 ? snapshots[0].SourcePath : ""),
                    LocationType: type,
                    FileCount: fileCount,
                    TotalSizeBytes: totalBytes,
                    LastModified: lastMod,
                    SnapshotCount: snapshots.Count,
                    LastBackupDate: lastBackup
                ));
            }
        }

        return result.OrderByDescending(t => t.LastBackupDate ?? t.LastModified ?? DateTime.MinValue).ToList();
    }

    /// <summary>
    /// Detects live save location for an App ID across Steam UserData, Documents, AppData, etc.
    /// </summary>
    public (SaveLocationType Type, string? Path, int FileCount, long TotalBytes, DateTime? LastModified)
        DetectSavePath(long appId, string gameName, string? installDir = null)
    {
        // 0. Check custom path override first
        string? custom = settings.GetCustomSavePath(appId);
        if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom))
        {
            var stats = GetDirectoryStats(custom);
            if (stats.Count > 0)
                return (SaveLocationType.Custom, custom, stats.Count, stats.Bytes, stats.LastMod);
        }

        // 1. Steam Userdata: <Steam>/userdata/<AccountId>/<AppId>
        string? steamRoot = steam.EffectivePath;
        if (!string.IsNullOrWhiteSpace(steamRoot))
        {
            string userdata = Path.Combine(steamRoot, "userdata");
            if (Directory.Exists(userdata))
            {
                try
                {
                    foreach (string userDir in Directory.EnumerateDirectories(userdata))
                    {
                        string gameUserdata = Path.Combine(userDir, appId.ToString());
                        if (Directory.Exists(gameUserdata))
                        {
                            string remote = Path.Combine(gameUserdata, "remote");
                            string candidate = Directory.Exists(remote) ? remote : gameUserdata;
                            var stats = GetDirectoryStats(candidate);
                            if (stats.Count > 0)
                            {
                                return (SaveLocationType.SteamUserData, candidate, stats.Count, stats.Bytes, stats.LastMod);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        string cleanName = SanitizeFolderName(gameName);

        // 2. Documents / My Games
        try
        {
            string myGames = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");
            if (Directory.Exists(myGames))
            {
                string p1 = Path.Combine(myGames, gameName);
                if (Directory.Exists(p1))
                {
                    var s = GetDirectoryStats(p1);
                    if (s.Count > 0) return (SaveLocationType.Documents, p1, s.Count, s.Bytes, s.LastMod);
                }
                if (!string.Equals(cleanName, gameName, StringComparison.OrdinalIgnoreCase))
                {
                    string p2 = Path.Combine(myGames, cleanName);
                    if (Directory.Exists(p2))
                    {
                        var s = GetDirectoryStats(p2);
                        if (s.Count > 0) return (SaveLocationType.Documents, p2, s.Count, s.Bytes, s.LastMod);
                    }
                }
            }

            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string pDocs = Path.Combine(docs, gameName);
            if (Directory.Exists(pDocs))
            {
                var s = GetDirectoryStats(pDocs);
                if (s.Count > 0) return (SaveLocationType.Documents, pDocs, s.Count, s.Bytes, s.LastMod);
            }
        }
        catch { }

        // 3. Saved Games (%USERPROFILE%/Saved Games)
        try
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string savedGames = Path.Combine(userProfile, "Saved Games");
            if (Directory.Exists(savedGames))
            {
                string p = Path.Combine(savedGames, gameName);
                if (Directory.Exists(p))
                {
                    var s = GetDirectoryStats(p);
                    if (s.Count > 0) return (SaveLocationType.SavedGames, p, s.Count, s.Bytes, s.LastMod);
                }
                string pClean = Path.Combine(savedGames, cleanName);
                if (Directory.Exists(pClean))
                {
                    var s = GetDirectoryStats(pClean);
                    if (s.Count > 0) return (SaveLocationType.SavedGames, pClean, s.Count, s.Bytes, s.LastMod);
                }
            }
        }
        catch { }

        // 4. Local AppData (%LOCALAPPDATA%/<GameName>)
        try
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pLocal = Path.Combine(localApp, gameName);
            if (Directory.Exists(pLocal))
            {
                var s = GetDirectoryStats(pLocal);
                if (s.Count > 0) return (SaveLocationType.AppDataLocal, pLocal, s.Count, s.Bytes, s.LastMod);
            }
            string pLocalClean = Path.Combine(localApp, cleanName);
            if (Directory.Exists(pLocalClean))
            {
                var s = GetDirectoryStats(pLocalClean);
                if (s.Count > 0) return (SaveLocationType.AppDataLocal, pLocalClean, s.Count, s.Bytes, s.LastMod);
            }
        }
        catch { }

        // 5. Roaming AppData (%APPDATA%/<GameName>)
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string pRoam = Path.Combine(appData, gameName);
            if (Directory.Exists(pRoam))
            {
                var s = GetDirectoryStats(pRoam);
                if (s.Count > 0) return (SaveLocationType.AppDataRoaming, pRoam, s.Count, s.Bytes, s.LastMod);
            }
            string pRoamClean = Path.Combine(appData, cleanName);
            if (Directory.Exists(pRoamClean))
            {
                var s = GetDirectoryStats(pRoamClean);
                if (s.Count > 0) return (SaveLocationType.AppDataRoaming, pRoamClean, s.Count, s.Bytes, s.LastMod);
            }
        }
        catch { }

        // 6. Game Install Directory (saves/savedata/steam_settings)
        if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
        {
            try
            {
                string[] candidates = ["saves", "savedata", "Save", "SaveGames", @"steam_settings\saves", @"steam_settings\storage"];
                foreach (var c in candidates)
                {
                    string sub = Path.Combine(installDir, c);
                    if (Directory.Exists(sub))
                    {
                        var s = GetDirectoryStats(sub);
                        if (s.Count > 0) return (SaveLocationType.InstallDir, sub, s.Count, s.Bytes, s.LastMod);
                    }
                }
            }
            catch { }
        }

        return (SaveLocationType.SteamUserData, null, 0, 0, null);
    }

    /// <summary>Creates a timestamped snapshot backup of a game's save folder.</summary>
    public SaveBackupSnapshot CreateBackupSnapshot(
        long appId,
        string gameName,
        string sourceDir,
        string? note = null,
        bool isSafety = false)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Save directory does not exist: {sourceDir}");

        string prefix = isSafety ? "safety_pre_restore" : "backup";
        string snapId = $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        string appBackupDir = GetAppBackupDir(appId);
        string snapshotDir = Path.Combine(appBackupDir, snapId);

        Directory.CreateDirectory(snapshotDir);

        // Copy all save files recursively
        CopyDirectoryRecursively(sourceDir, snapshotDir);

        var stats = GetDirectoryStats(snapshotDir);

        // Write manifest.json
        var manifest = new SnapshotManifest(
            SnapshotId: snapId,
            AppId: appId,
            GameName: gameName,
            Timestamp: DateTime.UtcNow,
            SourcePath: sourceDir,
            FileCount: stats.Count,
            TotalBytes: stats.Bytes,
            Note: note,
            IsSafety: isSafety
        );

        string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(snapshotDir, "manifest.json"), manifestJson);

        var snapshot = new SaveBackupSnapshot(
            SnapshotId: snapId,
            AppId: appId,
            GameName: gameName,
            Timestamp: manifest.Timestamp,
            DirectoryPath: snapshotDir,
            SourcePath: sourceDir,
            FileCount: stats.Count,
            TotalSizeBytes: stats.Bytes,
            Note: note,
            IsSafetyBackup: isSafety
        );

        // If auto-sync is enabled and this is a standard user backup, sync to cloud asynchronously
        if (settings.CloudSavesAutoSync && !isSafety)
        {
            _ = Task.Run(async () =>
            {
                try { await SyncToCloudAsync(); }
                catch { /* background sync best-effort */ }
            });
        }

        return snapshot;
    }

    /// <summary>List all snapshots for a specific game, sorted by newest first.</summary>
    public List<SaveBackupSnapshot> GetSnapshots(long appId)
    {
        string dir = GetAppBackupDir(appId);
        if (!Directory.Exists(dir)) return [];

        var list = new List<SaveBackupSnapshot>();

        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            string snapId = Path.GetFileName(sub);
            string manifestPath = Path.Combine(sub, "manifest.json");

            if (File.Exists(manifestPath))
            {
                try
                {
                    string json = File.ReadAllText(manifestPath);
                    var m = JsonSerializer.Deserialize<SnapshotManifest>(json);
                    if (m != null)
                    {
                        list.Add(new SaveBackupSnapshot(
                            SnapshotId: m.SnapshotId,
                            AppId: m.AppId,
                            GameName: m.GameName,
                            Timestamp: m.Timestamp,
                            DirectoryPath: sub,
                            SourcePath: m.SourcePath,
                            FileCount: m.FileCount,
                            TotalSizeBytes: m.TotalBytes,
                            Note: m.Note,
                            IsSafetyBackup: m.IsSafety
                        ));
                        continue;
                    }
                }
                catch { }
            }

            // Fallback for directory without valid manifest
            var stats = GetDirectoryStats(sub);
            bool isSafety = snapId.StartsWith("safety_pre_restore", StringComparison.OrdinalIgnoreCase);
            list.Add(new SaveBackupSnapshot(
                SnapshotId: snapId,
                AppId: appId,
                GameName: appId.ToString(),
                Timestamp: Directory.GetCreationTimeUtc(sub),
                DirectoryPath: sub,
                SourcePath: "",
                FileCount: stats.Count,
                TotalSizeBytes: stats.Bytes,
                Note: null,
                IsSafetyBackup: isSafety
            ));
        }

        return list.OrderByDescending(s => s.Timestamp).ToList();
    }

    /// <summary>
    /// Restores a snapshot into the live save directory. Automatically creates a safety pre-restore
    /// backup of the destination directory before overwriting!
    /// </summary>
    public void RestoreSnapshot(SaveBackupSnapshot snapshot, string? targetDir = null)
    {
        string destination = !string.IsNullOrWhiteSpace(targetDir) ? targetDir : snapshot.SourcePath;
        if (string.IsNullOrWhiteSpace(destination))
            throw new InvalidOperationException("No target directory specified for restore.");

        // Safety backup of existing files before restoring
        if (Directory.Exists(destination))
        {
            var stats = GetDirectoryStats(destination);
            if (stats.Count > 0)
            {
                CreateBackupSnapshot(
                    snapshot.AppId,
                    snapshot.GameName,
                    destination,
                    $"Safety backup before restoring snapshot {snapshot.SnapshotId}",
                    isSafety: true);
            }
        }
        else
        {
            Directory.CreateDirectory(destination);
        }

        // Copy files from snapshot into live save directory (skipping manifest.json)
        CopyDirectoryRecursively(snapshot.DirectoryPath, destination, excludeManifest: true);
    }

    /// <summary>Deletes a backup snapshot from disk.</summary>
    public void DeleteSnapshot(SaveBackupSnapshot snapshot)
    {
        if (Directory.Exists(snapshot.DirectoryPath))
        {
            Directory.Delete(snapshot.DirectoryPath, recursive: true);
        }
    }

    /// <summary>Sync local backup snapshots to the configured Cloud Provider.</summary>
    public async Task<CloudSyncResult> SyncToCloudAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        string provider = settings.CloudSavesProvider;
        progress?.Report($"Starting sync to {provider}…");

        switch (provider.ToLowerInvariant())
        {
            case "onedrive":
                return await SyncToOneDriveAsync(progress, ct);

            case "googledrive":
                return await SyncToGoogleDriveAsync(progress, ct);

            case "rclone":
                return await SyncWithRcloneAsync(toRemote: true, progress, ct);

            case "local":
            default:
                return SyncToLocalPath(progress);
        }
    }

    /// <summary>Restore backup snapshots from the configured Cloud Provider into local storage.</summary>
    public async Task<CloudSyncResult> SyncFromCloudAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        string provider = settings.CloudSavesProvider;
        progress?.Report($"Pulling saves from {provider}…");

        switch (provider.ToLowerInvariant())
        {
            case "onedrive":
                return await SyncFromOneDriveAsync(progress, ct);

            case "googledrive":
                return await SyncFromGoogleDriveAsync(progress, ct);

            case "rclone":
                return await SyncWithRcloneAsync(toRemote: false, progress, ct);

            case "local":
            default:
                return SyncFromLocalPath(progress);
        }
    }

    // ── OneDrive Sync ───────────────────────────────────────────────

    public string? ResolveOneDriveFolder()
    {
        string? env1 = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrWhiteSpace(env1) && Directory.Exists(env1)) return env1;

        string? env2 = Environment.GetEnvironmentVariable("OneDriveConsumer");
        if (!string.IsNullOrWhiteSpace(env2) && Directory.Exists(env2)) return env2;

        string? env3 = Environment.GetEnvironmentVariable("OneDriveCommercial");
        if (!string.IsNullOrWhiteSpace(env3) && Directory.Exists(env3)) return env3;

        string userProf = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string defaultPath = Path.Combine(userProf, "OneDrive");
        if (Directory.Exists(defaultPath)) return defaultPath;

        return null;
    }

    private Task<CloudSyncResult> SyncToOneDriveAsync(IProgress<string>? progress, CancellationToken ct)
    {
        string? oneDrive = ResolveOneDriveFolder();
        if (oneDrive == null)
        {
            // If native OneDrive desktop folder isn't found, check if rclone has onedrive remote
            string rclone = FindRcloneExe() ?? "";
            if (!string.IsNullOrEmpty(rclone))
            {
                progress?.Report("Native OneDrive folder not detected. Using rclone remote…");
                return SyncWithRcloneAsync(toRemote: true, progress, ct, remote: "onedrive:DataJackUI_Saves");
            }
            return Task.FromResult(new CloudSyncResult(false, "OneDrive folder was not found on this PC. Please configure OneDrive or use Rclone."));
        }

        string target = Path.Combine(oneDrive, "DataJackUI_Saves");
        progress?.Report($"Syncing to OneDrive: {target}");
        int copied = MirrorDirectory(BaseBackupDir, target);
        return Task.FromResult(new CloudSyncResult(true, $"Successfully synced {copied} file(s) to OneDrive.", copied));
    }

    private Task<CloudSyncResult> SyncFromOneDriveAsync(IProgress<string>? progress, CancellationToken ct)
    {
        string? oneDrive = ResolveOneDriveFolder();
        if (oneDrive == null)
        {
            string rclone = FindRcloneExe() ?? "";
            if (!string.IsNullOrEmpty(rclone))
            {
                return SyncWithRcloneAsync(toRemote: false, progress, ct, remote: "onedrive:DataJackUI_Saves");
            }
            return Task.FromResult(new CloudSyncResult(false, "OneDrive folder was not found on this PC."));
        }

        string source = Path.Combine(oneDrive, "DataJackUI_Saves");
        if (!Directory.Exists(source))
            return Task.FromResult(new CloudSyncResult(false, "No Cloud Saves found in OneDrive."));

        int copied = MirrorDirectory(source, BaseBackupDir);
        return Task.FromResult(new CloudSyncResult(true, $"Successfully restored {copied} file(s) from OneDrive.", copied));
    }

    // ── Google Drive Sync ───────────────────────────────────────────

    public string? ResolveGoogleDriveFolder()
    {
        // Check for Google Drive desktop virtual drive (e.g. G:\My Drive)
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady && (drive.VolumeLabel.Contains("Google", StringComparison.OrdinalIgnoreCase) || drive.Name.StartsWith("G", StringComparison.OrdinalIgnoreCase)))
                {
                    string candidate = Path.Combine(drive.RootDirectory.FullName, "My Drive");
                    if (Directory.Exists(candidate)) return candidate;
                    if (Directory.Exists(drive.RootDirectory.FullName)) return drive.RootDirectory.FullName;
                }
            }
        }
        catch { }

        return null;
    }

    private Task<CloudSyncResult> SyncToGoogleDriveAsync(IProgress<string>? progress, CancellationToken ct)
    {
        string? gdrive = ResolveGoogleDriveFolder();
        if (gdrive != null)
        {
            string target = Path.Combine(gdrive, "DataJackUI_Saves");
            progress?.Report($"Syncing to Google Drive folder: {target}");
            int copied = MirrorDirectory(BaseBackupDir, target);
            return Task.FromResult(new CloudSyncResult(true, $"Successfully synced {copied} file(s) to Google Drive.", copied));
        }

        // Fallback to rclone remote (default "gdrive:DataJackUI_Saves")
        progress?.Report("Local Google Drive client folder not found. Syncing via Rclone remote…");
        string remote = settings.CloudSavesRcloneRemote;
        return SyncWithRcloneAsync(toRemote: true, progress, ct, remote);
    }

    private Task<CloudSyncResult> SyncFromGoogleDriveAsync(IProgress<string>? progress, CancellationToken ct)
    {
        string? gdrive = ResolveGoogleDriveFolder();
        if (gdrive != null)
        {
            string source = Path.Combine(gdrive, "DataJackUI_Saves");
            if (!Directory.Exists(source))
                return Task.FromResult(new CloudSyncResult(false, "No Cloud Saves found in Google Drive folder."));

            int copied = MirrorDirectory(source, BaseBackupDir);
            return Task.FromResult(new CloudSyncResult(true, $"Successfully restored {copied} file(s) from Google Drive.", copied));
        }

        progress?.Report("Restoring from Google Drive via Rclone remote…");
        string remote = settings.CloudSavesRcloneRemote;
        return SyncWithRcloneAsync(toRemote: false, progress, ct, remote);
    }

    // ── Rclone Integration ──────────────────────────────────────────

    public string? FindRcloneExe()
    {
        // 1. User configured path
        if (!string.IsNullOrWhiteSpace(settings.CloudSavesRcloneExePath) && File.Exists(settings.CloudSavesRcloneExePath))
            return settings.CloudSavesRcloneExePath;

        // 2. Application BaseDirectory
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string direct = Path.Combine(baseDir, "rclone.exe");
        if (File.Exists(direct)) return direct;

        string tools = Path.Combine(baseDir, "tools", "rclone.exe");
        if (File.Exists(tools)) return tools;

        // 3. System PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (string p in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(p.Trim(), "rclone.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }

        return null;
    }

    /// <summary>
    /// Launches an interactive command terminal running `rclone config` so the user can easily
    /// authenticate Google Drive, OneDrive, Dropbox, or any of 70+ providers via browser OAuth.
    /// </summary>
    public bool LaunchRcloneConfig()
    {
        string rclone = FindRcloneExe() ?? "rclone";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"Rclone Configuration - DataJackUI\" cmd /k \"\"{rclone}\" config\"",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<CloudSyncResult> SyncWithRcloneAsync(
        bool toRemote,
        IProgress<string>? progress,
        CancellationToken ct,
        string? remote = null)
    {
        string? rcloneExe = FindRcloneExe();
        if (string.IsNullOrEmpty(rcloneExe))
        {
            return new CloudSyncResult(false, "rclone.exe was not found. Please install rclone or specify its path in settings.");
        }

        string effectiveRemote = remote ?? settings.CloudSavesRcloneRemote;
        string source = toRemote ? BaseBackupDir : effectiveRemote;
        string dest = toRemote ? effectiveRemote : BaseBackupDir;

        if (toRemote && !Directory.Exists(BaseBackupDir))
        {
            return new CloudSyncResult(true, "No local backups exist to sync.", 0);
        }

        progress?.Report($"Executing rclone sync: {source} -> {dest}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = rcloneExe,
                Arguments = $"sync \"{source}\" \"{dest}\" --stats-one-line -v",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) progress?.Report(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) progress?.Report(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode == 0)
            {
                return new CloudSyncResult(true, $"Rclone sync completed successfully ({dest}).");
            }
            return new CloudSyncResult(false, $"Rclone exited with code {proc.ExitCode}.");
        }
        catch (Exception ex)
        {
            return new CloudSyncResult(false, $"Rclone execution failed: {ex.Message}");
        }
    }

    // ── Local / Custom Folder Sync ──────────────────────────────────

    private CloudSyncResult SyncToLocalPath(IProgress<string>? progress)
    {
        string? path = settings.CloudSavesLocalPath;
        if (string.IsNullOrWhiteSpace(path))
            return new CloudSyncResult(false, "Custom local backup path has not been configured in settings.");

        Directory.CreateDirectory(path);
        int copied = MirrorDirectory(BaseBackupDir, path);
        return new CloudSyncResult(true, $"Successfully copied {copied} file(s) to {path}.", copied);
    }

    private CloudSyncResult SyncFromLocalPath(IProgress<string>? progress)
    {
        string? path = settings.CloudSavesLocalPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return new CloudSyncResult(false, "Custom local backup path does not exist.");

        int copied = MirrorDirectory(path, BaseBackupDir);
        return new CloudSyncResult(true, $"Successfully restored {copied} file(s) from {path}.", copied);
    }

    // ── Directory Helpers ───────────────────────────────────────────

    public static (int Count, long Bytes, DateTime? LastMod) GetDirectoryStats(string dir)
    {
        if (!Directory.Exists(dir)) return (0, 0, null);
        try
        {
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            int count = files.Length;
            long bytes = 0;
            DateTime? maxDate = null;

            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    bytes += fi.Length;
                    if (maxDate == null || fi.LastWriteTimeUtc > maxDate)
                        maxDate = fi.LastWriteTimeUtc;
                }
                catch { }
            }

            return (count, bytes, maxDate);
        }
        catch
        {
            return (0, 0, null);
        }
    }

    public static void CopyDirectoryRecursively(string sourceDir, string destDir, bool excludeManifest = false)
    {
        Directory.CreateDirectory(destDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            if (excludeManifest && Path.GetFileName(file).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                continue;

            string target = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }

        foreach (string sub in Directory.GetDirectories(sourceDir))
        {
            string targetSub = Path.Combine(destDir, Path.GetFileName(sub));
            CopyDirectoryRecursively(sub, targetSub, excludeManifest);
        }
    }

    public static int MirrorDirectory(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir)) return 0;
        Directory.CreateDirectory(targetDir);
        int count = 0;

        foreach (string srcFile in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, srcFile);
            string dstFile = Path.Combine(targetDir, rel);
            string? dstDir = Path.GetDirectoryName(dstFile);
            if (dstDir != null && !Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);

            var srcInfo = new FileInfo(srcFile);
            bool shouldCopy = true;

            if (File.Exists(dstFile))
            {
                var dstInfo = new FileInfo(dstFile);
                if (dstInfo.Length == srcInfo.Length && dstInfo.LastWriteTimeUtc >= srcInfo.LastWriteTimeUtc)
                {
                    shouldCopy = false;
                }
            }

            if (shouldCopy)
            {
                File.Copy(srcFile, dstFile, overwrite: true);
                count++;
            }
        }
        return count;
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return clean.Trim();
    }
}
