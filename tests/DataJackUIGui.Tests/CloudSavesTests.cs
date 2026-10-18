using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DataJackUIGui.Services;
using DataJackUIGui.ViewModels;
using Xunit;

namespace DataJackUIGui.Tests;

public class CloudSavesTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly SettingsService _settings;
    private readonly SteamService _steam;
    private readonly SteamLibraryService _steamLibrary;
    private readonly CloudSavesService _cloudSaves;

    public CloudSavesTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DataJackUI_CloudSavesTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _settings = new SettingsService();
        _steam = new SteamService(_settings);
        _steamLibrary = new SteamLibraryService(_steam);
        _cloudSaves = new CloudSavesService(_steam, _steamLibrary, _settings);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void CustomSavePath_CanBeConfiguredAndDetected()
    {
        long appId = 888801;
        string customDir = Path.Combine(_tempRoot, "CustomGameSave");
        Directory.CreateDirectory(customDir);
        File.WriteAllText(Path.Combine(customDir, "save.dat"), "save content");

        _settings.SetCustomSavePath(appId, customDir);

        var (type, path, count, bytes, lastMod) = _cloudSaves.DetectSavePath(appId, "TestGame");

        Assert.Equal(SaveLocationType.Custom, type);
        Assert.Equal(customDir, path);
        Assert.Equal(1, count);
        Assert.True(bytes > 0);
        Assert.NotNull(lastMod);

        // Reset custom path
        _settings.SetCustomSavePath(appId, null);
    }

    [Fact]
    public void CreateBackupSnapshot_CreatesDirectoryAndValidManifest()
    {
        long appId = 888802;
        string liveSaveDir = Path.Combine(_tempRoot, "LiveSaves_888802");
        Directory.CreateDirectory(liveSaveDir);
        File.WriteAllText(Path.Combine(liveSaveDir, "slot1.sav"), "Slot 1 Data");
        Directory.CreateDirectory(Path.Combine(liveSaveDir, "nested"));
        File.WriteAllText(Path.Combine(liveSaveDir, "nested", "slot2.sav"), "Slot 2 Nested Data");

        var snapshot = _cloudSaves.CreateBackupSnapshot(appId, "Super Game", liveSaveDir, "Test backup note");

        try
        {
            Assert.NotNull(snapshot);
            Assert.StartsWith("backup_", snapshot.SnapshotId);
            Assert.Equal(appId, snapshot.AppId);
            Assert.Equal("Super Game", snapshot.GameName);
            Assert.Equal(2, snapshot.FileCount);
            Assert.False(snapshot.IsSafetyBackup);
            Assert.Equal("Test backup note", snapshot.Note);

            Assert.True(Directory.Exists(snapshot.DirectoryPath));
            Assert.True(File.Exists(Path.Combine(snapshot.DirectoryPath, "slot1.sav")));
            Assert.True(File.Exists(Path.Combine(snapshot.DirectoryPath, "nested", "slot2.sav")));
            Assert.True(File.Exists(Path.Combine(snapshot.DirectoryPath, "manifest.json")));

            // Check manifest.json contents
            string json = File.ReadAllText(Path.Combine(snapshot.DirectoryPath, "manifest.json"));
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(appId, doc.RootElement.GetProperty("AppId").GetInt64());
            Assert.Equal(2, doc.RootElement.GetProperty("FileCount").GetInt32());
            Assert.False(doc.RootElement.GetProperty("IsSafety").GetBoolean());
        }
        finally
        {
            _cloudSaves.DeleteSnapshot(snapshot);
        }
    }

    [Fact]
    public void GetSnapshots_ReturnsSnapshotsOrderedByTimestamp()
    {
        long appId = 888803;
        string liveSaveDir = Path.Combine(_tempRoot, "LiveSaves_888803");
        Directory.CreateDirectory(liveSaveDir);
        File.WriteAllText(Path.Combine(liveSaveDir, "progress.sav"), "Progress A");

        var snap1 = _cloudSaves.CreateBackupSnapshot(appId, "OrderGame", liveSaveDir, "First");
        System.Threading.Thread.Sleep(1100); // ensure distinct second timestamp
        File.WriteAllText(Path.Combine(liveSaveDir, "progress.sav"), "Progress B");
        var snap2 = _cloudSaves.CreateBackupSnapshot(appId, "OrderGame", liveSaveDir, "Second");

        try
        {
            var snapshots = _cloudSaves.GetSnapshots(appId);
            Assert.True(snapshots.Count >= 2);
            // Newest first
            Assert.Equal(snap2.SnapshotId, snapshots[0].SnapshotId);
            Assert.Equal(snap1.SnapshotId, snapshots[1].SnapshotId);
        }
        finally
        {
            _cloudSaves.DeleteSnapshot(snap1);
            _cloudSaves.DeleteSnapshot(snap2);
        }
    }

    [Fact]
    public void RestoreSnapshot_CreatesAutomaticSafetyBackup_AndRestoresOriginalFiles()
    {
        long appId = 888804;
        string liveSaveDir = Path.Combine(_tempRoot, "LiveSaves_888804");
        Directory.CreateDirectory(liveSaveDir);
        string file1 = Path.Combine(liveSaveDir, "save.dat");
        File.WriteAllText(file1, "ORIGINAL GOOD SAVE DATA");

        var originalSnapshot = _cloudSaves.CreateBackupSnapshot(appId, "RestoreGame", liveSaveDir, "Good Save");

        // Now simulate user corruption or changed save state
        File.WriteAllText(file1, "CORRUPTED OR MODIFIED SAVE DATA");

        // Restore snapshot
        _cloudSaves.RestoreSnapshot(originalSnapshot, liveSaveDir);

        // Verify live save has been restored
        string restoredText = File.ReadAllText(file1);
        Assert.Equal("ORIGINAL GOOD SAVE DATA", restoredText);

        // Verify safety backup was automatically created!
        var allSnaps = _cloudSaves.GetSnapshots(appId);
        var safetyBackup = allSnaps.Find(s => s.IsSafetyBackup);
        Assert.NotNull(safetyBackup);
        Assert.StartsWith("safety_pre_restore_", safetyBackup.SnapshotId);

        // Verify safety backup contains the modified state
        string safetyFile = Path.Combine(safetyBackup.DirectoryPath, "save.dat");
        Assert.True(File.Exists(safetyFile));
        Assert.Equal("CORRUPTED OR MODIFIED SAVE DATA", File.ReadAllText(safetyFile));

        // Cleanup
        foreach (var s in allSnaps)
        {
            _cloudSaves.DeleteSnapshot(s);
        }
    }

    [Fact]
    public void MirrorDirectory_MirrorsAllFilesAndSubfolders()
    {
        string src = Path.Combine(_tempRoot, "MirrorSource");
        string dst = Path.Combine(_tempRoot, "MirrorTarget");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(Path.Combine(src, "folderA"));

        File.WriteAllText(Path.Combine(src, "root.txt"), "Root File");
        File.WriteAllText(Path.Combine(src, "folderA", "child.txt"), "Child File");

        int copied = CloudSavesService.MirrorDirectory(src, dst);

        Assert.Equal(2, copied);
        Assert.True(File.Exists(Path.Combine(dst, "root.txt")));
        Assert.True(File.Exists(Path.Combine(dst, "folderA", "child.txt")));
        Assert.Equal("Child File", File.ReadAllText(Path.Combine(dst, "folderA", "child.txt")));

        // Second mirror with no changes should copy 0 files
        int copiedAgain = CloudSavesService.MirrorDirectory(src, dst);
        Assert.Equal(0, copiedAgain);
    }

    [Fact]
    public void CloudSavesViewModel_ProviderSettings_UpdateCorrectly()
    {
        var vm = new CloudSavesViewModel(
            _cloudSaves,
            _settings,
            new SteamAppInfoCache(new CacheService()),
            new CoverCache(_settings),
            new ToastService());

        vm.SelectedProvider = "Rclone";
        Assert.True(vm.IsRcloneProvider);
        Assert.False(vm.IsOneDriveProvider);
        Assert.Equal("Rclone", _settings.CloudSavesProvider);

        vm.SelectedProvider = "OneDrive";
        Assert.True(vm.IsOneDriveProvider);
        Assert.False(vm.IsRcloneProvider);
        Assert.Equal("OneDrive", _settings.CloudSavesProvider);

        vm.AutoSync = true;
        Assert.True(_settings.CloudSavesAutoSync);

        vm.RcloneRemote = "testremote:my_saves";
        Assert.Equal("testremote:my_saves", _settings.CloudSavesRcloneRemote);
    }

    [Fact]
    public void OAuth_GoogleDriveAndOneDrive_ConnectAndDisconnect_UpdatesSettingsAndState()
    {
        // Google Drive
        Assert.False(_cloudSaves.IsGoogleDriveConnected);
        Assert.Null(_cloudSaves.GetGoogleDriveAccount());

        _settings.CloudSavesGDriveAccount = "gamer@gmail.com";

        Assert.True(_cloudSaves.IsGoogleDriveConnected);
        Assert.Equal("gamer@gmail.com", _cloudSaves.GetGoogleDriveAccount());

        _cloudSaves.DisconnectGoogleDrive();
        Assert.False(_cloudSaves.IsGoogleDriveConnected);
        Assert.Null(_cloudSaves.GetGoogleDriveAccount());

        // OneDrive
        Assert.False(_cloudSaves.IsOneDriveConnected);
        Assert.Null(_cloudSaves.GetOneDriveAccount());

        _settings.CloudSavesOneDriveAccount = "gamer@outlook.com";

        Assert.True(_cloudSaves.IsOneDriveConnected);
        Assert.Equal("gamer@outlook.com", _cloudSaves.GetOneDriveAccount());

        _cloudSaves.DisconnectOneDrive();
        Assert.False(_cloudSaves.IsOneDriveConnected);
        Assert.Null(_cloudSaves.GetOneDriveAccount());
    }

    [Fact]
    public void CloudSavesViewModel_OAuthStateAndCommands_ReflectProperly()
    {
        _settings.CloudSavesGDriveAccount = "pilot@gmail.com";
        _settings.CloudSavesOneDriveAccount = "pilot@outlook.com";

        var vm = new CloudSavesViewModel(
            _cloudSaves,
            _settings,
            new SteamAppInfoCache(new CacheService()),
            new CoverCache(_settings),
            new ToastService());

        Assert.True(vm.IsGoogleDriveConnected);
        Assert.Equal("pilot@gmail.com", vm.GoogleDriveAccount);
        Assert.Contains("pilot@gmail.com", vm.GoogleDriveStatus);

        Assert.True(vm.IsOneDriveConnected);
        Assert.Equal("pilot@outlook.com", vm.OneDriveAccount);
        Assert.Contains("pilot@outlook.com", vm.OneDriveStatus);

        vm.DisconnectGoogleDriveCommand.Execute(null);
        Assert.False(vm.IsGoogleDriveConnected);

        vm.DisconnectOneDriveCommand.Execute(null);
        Assert.False(vm.IsOneDriveConnected);
    }

    [Fact]
    public void BaseBackupDir_FollowsCustomLocalBackupPath()
    {
        string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUI", "save_backups");
        _settings.CloudSavesLocalPath = null;
        Assert.Equal(defaultPath, _cloudSaves.BaseBackupDir);

        string customPath = Path.Combine(_tempRoot, "MyCustomBackups");
        _settings.CloudSavesLocalPath = customPath;
        Assert.Equal(Path.GetFullPath(customPath), _cloudSaves.BaseBackupDir);

        // Reset
        _settings.CloudSavesLocalPath = null;
        Assert.Equal(defaultPath, _cloudSaves.BaseBackupDir);
    }

    [Fact]
    public void CloudGameItemViewModel_WithInitialCover_InitializesImmediately()
    {
        var target = new GameSaveTarget(
            413150,
            "Stardew Valley",
            @"C:\Saves\Stardew",
            SaveLocationType.AppDataRoaming,
            3,
            1024,
            DateTime.UtcNow,
            1,
            DateTime.UtcNow);

        string testCover = @"C:\Covers\413150.jpg";
        var vm = new CloudGameItemViewModel(target, testCover);

        Assert.Equal(413150, vm.AppId);
        Assert.Equal("Stardew Valley", vm.GameName);
        Assert.Equal(testCover, vm.CoverPath);
        Assert.True(vm.HasBackups);
        Assert.Equal("Roaming AppData", vm.LocationDisplay);
    }
}
