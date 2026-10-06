using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DataJackUIGui.Models;
using DataJackUIGui.Services;
using Xunit;

namespace DataJackUIGui.Tests;

public class AuthStorageTests : IDisposable
{
    private readonly string _tempFile;
    private readonly string _originalAuthFile;

    public AuthStorageTests()
    {
        _originalAuthFile = AuthService.AuthFile;
        _tempFile = Path.Combine(Path.GetTempPath(), $"datajackui_test_auth_{Guid.NewGuid():N}.dat");
        AuthService.AuthFile = _tempFile;
    }

    public void Dispose()
    {
        AuthService.AuthFile = _originalAuthFile;
        if (File.Exists(_tempFile))
        {
            try { File.Delete(_tempFile); } catch { }
        }
    }

    [Fact]
    public void SaveStored_And_LoadStored_RoundTrip_PreservesSessionAndProfile()
    {
        var auth = new StoredAuth
        {
            AccessToken = "test-access-token-xyz-123",
            RefreshToken = "test-refresh-token-abc-456",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            Username = "TestRunner",
            UserId = "user-123456",
            AvatarUrl = "https://cdn.example.com/avatar.png"
        };

        AuthService.SaveStored(auth);

        Assert.True(File.Exists(_tempFile));

        var loaded = AuthService.LoadStored();
        Assert.NotNull(loaded);
        Assert.Equal(auth.AccessToken, loaded.AccessToken);
        Assert.Equal(auth.RefreshToken, loaded.RefreshToken);
        Assert.Equal(auth.Username, loaded.Username);
        Assert.Equal(auth.UserId, loaded.UserId);
        Assert.Equal(auth.AvatarUrl, loaded.AvatarUrl);
    }

    [Fact]
    public void SaveStored_EncryptedOutput_CannotBeDecryptedByPlainDpapi()
    {
        var auth = new StoredAuth
        {
            AccessToken = "secret-token",
            RefreshToken = "secret-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Username = "SecureUser",
            UserId = "sec-001"
        };

        AuthService.SaveStored(auth);

        byte[] rawBytes = File.ReadAllBytes(_tempFile);

        // Standard DPAPI infostealer payload unprotect (entropy = null) MUST fail
        Assert.ThrowsAny<CryptographicException>(() =>
            ProtectedData.Unprotect(rawBytes, null, DataProtectionScope.CurrentUser));
    }

    [Fact]
    public void LegacyStorage_GracefullyMigratesToModernFormat()
    {
        var legacyAuth = new StoredAuth
        {
            AccessToken = "legacy-access",
            RefreshToken = "legacy-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(3),
            Username = "LegacyRunner",
            UserId = "legacy-999"
        };

        // Write legacy plain DPAPI (null entropy)
        byte[] legacyPlain = JsonSerializer.SerializeToUtf8Bytes(legacyAuth);
        byte[] legacyEnc = ProtectedData.Protect(legacyPlain, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_tempFile, legacyEnc);

        // Load should succeed
        var loaded = AuthService.LoadStored();
        Assert.NotNull(loaded);
        Assert.Equal("legacy-access", loaded.AccessToken);
        Assert.Equal("LegacyRunner", loaded.Username);

        // Verify that the file was upgraded so standard DPAPI dumpers can no longer decrypt it
        byte[] upgradedRawBytes = File.ReadAllBytes(_tempFile);
        Assert.ThrowsAny<CryptographicException>(() =>
            ProtectedData.Unprotect(upgradedRawBytes, null, DataProtectionScope.CurrentUser));
    }

    [Fact]
    public void StoredAuth_DoesNotContainPII()
    {
        var auth = new StoredAuth
        {
            AccessToken = "tok",
            RefreshToken = "ref",
            Username = "UserX",
            UserId = "12345"
        };

        string json = JsonSerializer.Serialize(auth);

        Assert.DoesNotContain("Email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DiscordId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DisplayName", json, StringComparison.OrdinalIgnoreCase);
    }
}
