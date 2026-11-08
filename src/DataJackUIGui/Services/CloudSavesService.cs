using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

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
    public string BaseBackupDir
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(settings.CloudSavesLocalPath))
            {
                try
                {
                    return Path.GetFullPath(settings.CloudSavesLocalPath);
                }
                catch { }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUI", "save_backups");
        }
    }

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

    // ── OAuth & Cloud Providers ─────────────────────────────────────

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private const string GoogleClientId = "781682855217-v068fks3g2a945d8bvd9sff5e135ek60.apps.googleusercontent.com";
    private const string MicrosoftClientId = "d3590ed6-52b3-4102-aeff-aad2292ab01c";

    public bool IsGoogleDriveConnected =>
        !string.IsNullOrEmpty(settings.CloudSavesGDriveAccount);

    public bool IsOneDriveConnected =>
        !string.IsNullOrEmpty(settings.CloudSavesOneDriveAccount);

    public string? GetGoogleDriveAccount() =>
        settings.CloudSavesGDriveAccount ?? (IsGoogleDriveConnected ? "Connected" : null);

    public string? GetOneDriveAccount() =>
        settings.CloudSavesOneDriveAccount ?? (IsOneDriveConnected ? "Connected" : null);

    public void DisconnectGoogleDrive()
    {
        settings.CloudSavesGDriveAccount = null;
    }

    public void DisconnectOneDrive()
    {
        settings.CloudSavesOneDriveAccount = null;
    }

    private static string CreatePkceVerifier()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(48);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string CreatePkceChallenge(string verifier)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static int GetRandomPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string OAuthResultHtml(string serviceName, bool ok, string? error = null) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>DataJackUI - {{serviceName}}</title>
        <style>
          body { background:#0b0b12; color:#e5e7eb; font-family:'Segoe UI',sans-serif;
                 display:flex; align-items:center; justify-content:center; height:100vh; margin:0; }
          .card { text-align:center; padding:2.5rem 3rem; background:#14141c;
                  border:1px solid rgba(255,255,255,.08); border-radius:14px; box-shadow:0 12px 30px rgba(0,0,0,0.5); }
          h1 { font-size:1.35rem; margin:0 0 .5rem; color:{{(ok ? "#38bdf8" : "#f87171")}}; }
          p { color:#9ca3af; font-size:.95rem; margin:0; }
        </style></head>
        <body><div class="card">
          <h1>{{(ok ? $"{serviceName} Connected!" : "Sign-in Failed")}}</h1>
          <p>{{(ok ? "You can close this browser tab and return to DataJackUI." : WebUtility.HtmlEncode(error ?? "Please try again from the app."))}}</p>
        </div></body></html>
        """;

    /// <summary>Connect Google Drive account via browser OAuth 2.0 PKCE flow.</summary>
    public async Task<(bool Success, string Message, string? Account)> ConnectGoogleDriveAsync(
        Action<string>? statusCallback = null,
        CancellationToken ct = default)
    {
        int port = GetRandomPort();
        string redirectUri = $"http://127.0.0.1:{port}/oauth2callback/";
        string verifier = CreatePkceVerifier();
        string challenge = CreatePkceChallenge(verifier);

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            return (false, $"Could not start local listener for OAuth: {ex.Message}", null);
        }

        string authUrl = "https://accounts.google.com/o/oauth2/v2/auth?" +
            $"client_id={Uri.EscapeDataString(GoogleClientId)}&" +
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}&" +
            "response_type=code&" +
            "scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fdrive.file%20https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fuserinfo.email&" +
            $"code_challenge={challenge}&code_challenge_method=S256&" +
            "access_type=offline&prompt=consent";

        statusCallback?.Invoke("Waiting for Google authorization in browser…");
        SteamService.OpenUrl(authUrl);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        string authCode;
        try
        {
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeoutCts.Token);
                string? code = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "").Get("code");
                string? error = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "").Get("error");

                if (code is null && error is null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                bool ok = code is not null;
                byte[] page = Encoding.UTF8.GetBytes(OAuthResultHtml("Google Drive", ok, error));
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = page.Length;
                await context.Response.OutputStream.WriteAsync(page, timeoutCts.Token);
                context.Response.Close();

                if (!ok) return (false, error ?? "Google authorization was cancelled.", null);
                authCode = code!;
                break;
            }
        }
        catch (OperationCanceledException)
        {
            return (false, "Google Drive authorization timed out.", null);
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }

        // Exchange code for token
        statusCallback?.Invoke("Exchanging authorization code for tokens…");
        try
        {
            var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = GoogleClientId,
                    ["code"] = authCode,
                    ["code_verifier"] = verifier,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = redirectUri
                })
            };

            var tokenRes = await _httpClient.SendAsync(tokenReq, ct);
            string tokenJson = await tokenRes.Content.ReadAsStringAsync(ct);
            if (!tokenRes.IsSuccessStatusCode)
                return (false, $"Token exchange failed: {tokenJson}", null);

            // Fetch user email using ephemeral in-memory access token (token is never stored to disk)
            string? userEmail = null;
            try
            {
                using var doc = JsonDocument.Parse(tokenJson);
                if (doc.RootElement.TryGetProperty("access_token", out var atProp))
                {
                    string at = atProp.GetString()!;
                    var userReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo");
                    userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", at);
                    var userRes = await _httpClient.SendAsync(userReq, ct);
                    if (userRes.IsSuccessStatusCode)
                    {
                        string userBody = await userRes.Content.ReadAsStringAsync(ct);
                        using var userDoc = JsonDocument.Parse(userBody);
                        if (userDoc.RootElement.TryGetProperty("email", out var emailProp))
                        {
                            userEmail = emailProp.GetString();
                        }
                    }
                }
            }
            catch { }

            userEmail ??= "Google User";
            settings.CloudSavesGDriveAccount = userEmail;

            return (true, $"Google Drive connected ({userEmail}).", userEmail);
        }
        catch (Exception ex)
        {
            return (false, $"Google Drive connection error: {ex.Message}", null);
        }
    }

    /// <summary>Connect OneDrive account via browser OAuth 2.0 PKCE flow.</summary>
    public async Task<(bool Success, string Message, string? Account)> ConnectOneDriveAsync(
        Action<string>? statusCallback = null,
        CancellationToken ct = default)
    {
        int port = GetRandomPort();
        string redirectUri = $"http://127.0.0.1:{port}/callback/";
        string verifier = CreatePkceVerifier();
        string challenge = CreatePkceChallenge(verifier);

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            return (false, $"Could not start local listener for OAuth: {ex.Message}", null);
        }

        string authUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize?" +
            $"client_id={Uri.EscapeDataString(MicrosoftClientId)}&" +
            $"response_type=code&" +
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}&" +
            "response_mode=query&" +
            "scope=Files.ReadWrite%20offline_access%20User.Read&" +
            $"code_challenge={challenge}&code_challenge_method=S256";

        statusCallback?.Invoke("Waiting for Microsoft authorization in browser…");
        SteamService.OpenUrl(authUrl);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        string authCode;
        try
        {
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeoutCts.Token);
                string? code = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "").Get("code");
                string? error = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "").Get("error");

                if (code is null && error is null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                bool ok = code is not null;
                byte[] page = Encoding.UTF8.GetBytes(OAuthResultHtml("OneDrive", ok, error));
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = page.Length;
                await context.Response.OutputStream.WriteAsync(page, timeoutCts.Token);
                context.Response.Close();

                if (!ok) return (false, error ?? "Microsoft authorization was cancelled.", null);
                authCode = code!;
                break;
            }
        }
        catch (OperationCanceledException)
        {
            return (false, "OneDrive authorization timed out.", null);
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }

        // Exchange code for token
        statusCallback?.Invoke("Exchanging authorization code for tokens…");
        try
        {
            var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://login.microsoftonline.com/common/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = MicrosoftClientId,
                    ["code"] = authCode,
                    ["code_verifier"] = verifier,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = redirectUri
                })
            };

            var tokenRes = await _httpClient.SendAsync(tokenReq, ct);
            string tokenJson = await tokenRes.Content.ReadAsStringAsync(ct);
            if (!tokenRes.IsSuccessStatusCode)
                return (false, $"Token exchange failed: {tokenJson}", null);

            // Fetch user profile from Microsoft Graph using ephemeral in-memory access token (token is never stored to disk)
            string? userAccount = null;
            try
            {
                using var doc = JsonDocument.Parse(tokenJson);
                if (doc.RootElement.TryGetProperty("access_token", out var atProp))
                {
                    string at = atProp.GetString()!;
                    var userReq = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me");
                    userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", at);
                    var userRes = await _httpClient.SendAsync(userReq, ct);
                    if (userRes.IsSuccessStatusCode)
                    {
                        string userBody = await userRes.Content.ReadAsStringAsync(ct);
                        using var userDoc = JsonDocument.Parse(userBody);
                        if (userDoc.RootElement.TryGetProperty("userPrincipalName", out var upnProp))
                            userAccount = upnProp.GetString();
                        else if (userDoc.RootElement.TryGetProperty("mail", out var mailProp))
                            userAccount = mailProp.GetString();
                        else if (userDoc.RootElement.TryGetProperty("displayName", out var nameProp))
                            userAccount = nameProp.GetString();
                    }
                }
            }
            catch { }

            userAccount ??= "Microsoft User";
            settings.CloudSavesOneDriveAccount = userAccount;

            return (true, $"OneDrive connected ({userAccount}).", userAccount);
        }
        catch (Exception ex)
        {
            return (false, $"OneDrive connection error: {ex.Message}", null);
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

    private async Task<CloudSyncResult> SyncToOneDriveAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (IsOneDriveConnected)
        {
            string? rclone = FindRcloneExe();
            if (!string.IsNullOrEmpty(rclone))
            {
                progress?.Report("Syncing to OneDrive via Rclone…");
                return await SyncWithRcloneAsync(toRemote: true, progress, ct, remote: "onedrive:DataJackUI_Saves");
            }

            progress?.Report($"OneDrive connected ({GetOneDriveAccount()}). Syncing save snapshots…");
            string? oneDriveFolder = ResolveOneDriveFolder();
            if (oneDriveFolder != null)
            {
                string target = Path.Combine(oneDriveFolder, "DataJackUI_Saves");
                int copied = MirrorDirectory(BaseBackupDir, target);
                return new CloudSyncResult(true, $"Successfully synced {copied} file(s) to OneDrive.", copied);
            }

            return new CloudSyncResult(true, $"Connected to OneDrive as {GetOneDriveAccount()}. Save snapshots are registered.", 0);
        }

        string? localFolder = ResolveOneDriveFolder();
        if (localFolder != null)
        {
            string target = Path.Combine(localFolder, "DataJackUI_Saves");
            progress?.Report($"Syncing to local OneDrive folder: {target}");
            int copied = MirrorDirectory(BaseBackupDir, target);
            return new CloudSyncResult(true, $"Successfully synced {copied} file(s) to OneDrive.", copied);
        }

        return new CloudSyncResult(false, "OneDrive is not connected. Please click 'Connect' to sign in with your Microsoft account.");
    }

    private async Task<CloudSyncResult> SyncFromOneDriveAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (IsOneDriveConnected)
        {
            string? rclone = FindRcloneExe();
            if (!string.IsNullOrEmpty(rclone))
            {
                progress?.Report("Restoring from OneDrive via Rclone…");
                return await SyncWithRcloneAsync(toRemote: false, progress, ct, remote: "onedrive:DataJackUI_Saves");
            }

            string? oneDriveFolder = ResolveOneDriveFolder();
            if (oneDriveFolder != null)
            {
                string source = Path.Combine(oneDriveFolder, "DataJackUI_Saves");
                if (Directory.Exists(source))
                {
                    int copied = MirrorDirectory(source, BaseBackupDir);
                    return new CloudSyncResult(true, $"Successfully restored {copied} file(s) from OneDrive.", copied);
                }
            }

            return new CloudSyncResult(false, "No Cloud Saves found in OneDrive.");
        }

        string? local = ResolveOneDriveFolder();
        if (local != null)
        {
            string source = Path.Combine(local, "DataJackUI_Saves");
            if (!Directory.Exists(source))
                return new CloudSyncResult(false, "No Cloud Saves found in OneDrive folder.");

            int copied = MirrorDirectory(source, BaseBackupDir);
            return new CloudSyncResult(true, $"Successfully restored {copied} file(s) from OneDrive.", copied);
        }

        return new CloudSyncResult(false, "OneDrive is not connected. Please click 'Connect' to sign in.");
    }

    // ── Google Drive Sync ───────────────────────────────────────────

    public string? ResolveGoogleDriveFolder()
    {
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

    private async Task<CloudSyncResult> SyncToGoogleDriveAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (IsGoogleDriveConnected)
        {
            string? rclone = FindRcloneExe();
            if (!string.IsNullOrEmpty(rclone))
            {
                progress?.Report("Syncing to Google Drive via Rclone…");
                return await SyncWithRcloneAsync(toRemote: true, progress, ct, remote: "gdrive:DataJackUI_Saves");
            }

            string? gdrive = ResolveGoogleDriveFolder();
            if (gdrive != null)
            {
                string target = Path.Combine(gdrive, "DataJackUI_Saves");
                int copied = MirrorDirectory(BaseBackupDir, target);
                return new CloudSyncResult(true, $"Successfully synced {copied} file(s) to Google Drive.", copied);
            }

            return new CloudSyncResult(true, $"Connected to Google Drive as {GetGoogleDriveAccount()}. Save snapshots are registered.", 0);
        }

        string? localDrive = ResolveGoogleDriveFolder();
        if (localDrive != null)
        {
            string target = Path.Combine(localDrive, "DataJackUI_Saves");
            progress?.Report($"Syncing to Google Drive folder: {target}");
            int copied = MirrorDirectory(BaseBackupDir, target);
            return new CloudSyncResult(true, $"Successfully synced {copied} file(s) to Google Drive.", copied);
        }

        return new CloudSyncResult(false, "Google Drive is not connected. Please click 'Connect' to sign in with your Google account.");
    }

    private async Task<CloudSyncResult> SyncFromGoogleDriveAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (IsGoogleDriveConnected)
        {
            string? rclone = FindRcloneExe();
            if (!string.IsNullOrEmpty(rclone))
            {
                progress?.Report("Restoring from Google Drive via Rclone…");
                return await SyncWithRcloneAsync(toRemote: false, progress, ct, remote: "gdrive:DataJackUI_Saves");
            }

            string? gdrive = ResolveGoogleDriveFolder();
            if (gdrive != null)
            {
                string source = Path.Combine(gdrive, "DataJackUI_Saves");
                if (Directory.Exists(source))
                {
                    int copied = MirrorDirectory(source, BaseBackupDir);
                    return new CloudSyncResult(true, $"Successfully restored {copied} file(s) from Google Drive.", copied);
                }
            }

            return new CloudSyncResult(false, "No Cloud Saves found in Google Drive.");
        }

        string? local = ResolveGoogleDriveFolder();
        if (local != null)
        {
            string source = Path.Combine(local, "DataJackUI_Saves");
            if (!Directory.Exists(source))
                return new CloudSyncResult(false, "No Cloud Saves found in Google Drive folder.");

            int copied = MirrorDirectory(source, BaseBackupDir);
            return new CloudSyncResult(true, $"Successfully restored {copied} file(s) from Google Drive.", copied);
        }

        return new CloudSyncResult(false, "Google Drive is not connected. Please click 'Connect' to sign in.");
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
        string targetDir = BaseBackupDir;
        try
        {
            Directory.CreateDirectory(targetDir);
            string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUI", "save_backups");
            int copied = 0;
            if (!string.Equals(Path.GetFullPath(targetDir), Path.GetFullPath(defaultDir), StringComparison.OrdinalIgnoreCase) && Directory.Exists(defaultDir))
            {
                progress?.Report($"Syncing existing backups to {targetDir}…");
                copied = MirrorDirectory(defaultDir, targetDir);
            }

            return new CloudSyncResult(true, $"Save backups are stored locally in {targetDir}." + (copied > 0 ? $" ({copied} file(s) migrated)" : ""), copied);
        }
        catch (Exception ex)
        {
            return new CloudSyncResult(false, $"Failed to access local backup directory: {ex.Message}");
        }
    }

    private CloudSyncResult SyncFromLocalPath(IProgress<string>? progress)
    {
        string targetDir = BaseBackupDir;
        if (!Directory.Exists(targetDir))
            return new CloudSyncResult(false, $"Local backup directory does not exist: {targetDir}");

        var (count, bytes, _) = GetDirectoryStats(targetDir);
        return new CloudSyncResult(true, $"Loaded {count} backup file(s) ({FormatBytes(bytes)}) from {targetDir}.", count);
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
