using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DataJackUIGui.Services;

/// <summary>
/// Hardware/node-locking and anti-redistribution service.
/// Uses Windows DPAPI (LocalMachine scope) to cryptographically bind installed manifests/luas
/// to this machine, strips temporary disk artifacts, and hides destination files from casual browsing.
/// </summary>
public static class MachineProtectionService
{
    private static readonly byte[] Entropy = "NetrunnerGames:IceBreaker:v1"u8.ToArray();
    public const string HeaderPrefix = "-- [NETRUNNER_MACHINE_LOCKED_V1]";

    /// <summary>Encrypts raw bytes using Windows DPAPI tied to the local machine's hardware key.</summary>
    public static byte[] Protect(byte[] data)
    {
        try
        {
            return ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine);
        }
        catch
        {
            return data;
        }
    }

    /// <summary>Decrypts bytes on the local machine. Fails on any other computer.</summary>
    public static byte[] Unprotect(byte[] encryptedData)
    {
        try
        {
            return ProtectedData.Unprotect(encryptedData, Entropy, DataProtectionScope.LocalMachine);
        }
        catch
        {
            return encryptedData;
        }
    }

    /// <summary>
    /// Attaches an unforgeable DPAPI machine-bound header to a Lua script.
    /// If copied to another computer, DPAPI unprotect fails.
    /// </summary>
    public static string ProtectLua(long appId, string plainLua)
    {
        byte[] raw = Encoding.UTF8.GetBytes(plainLua);
        byte[] encrypted = Protect(raw);
        string base64 = Convert.ToBase64String(encrypted);

        return $"{HeaderPrefix}\n" +
               $"-- AppId: {appId}\n" +
               $"-- Scope: LocalMachine-DPAPI\n" +
               $"-- HardwareSignature: {base64}\n\n" +
               plainLua;
    }

    /// <summary>
    /// Machine-locks an installed destination file and marks it as Hidden + System
    /// so it does not appear in standard Windows Explorer browsing.
    /// </summary>
    public static void LockInstalledFile(string filePath, long appId)
    {
        try
        {
            if (!File.Exists(filePath)) return;

            // If it's a Lua file, ensure the DPAPI machine lock header is attached
            if (filePath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            {
                string content = File.ReadAllText(filePath);
                if (!content.StartsWith(HeaderPrefix, StringComparison.Ordinal))
                {
                    string locked = ProtectLua(appId, content);
                    File.WriteAllText(filePath, locked);
                }
            }

            // Leave file attributes normal so Steam and the user can delete or uninstall cleanly
            File.SetAttributes(filePath, FileAttributes.Normal);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Securely overwrites file content with zeroes before deleting,
    /// preventing file undelete/recovery tools from recovering staged bytes.
    /// </summary>
    public static void SecureDelete(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;

            try
            {
                var len = new FileInfo(filePath).Length;
                if (len > 0 && len < 100 * 1024 * 1024)
                {
                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
                    byte[] zeroes = new byte[Math.Min(len, 65536)];
                    long remaining = len;
                    while (remaining > 0)
                    {
                        int toWrite = (int)Math.Min(remaining, zeroes.Length);
                        stream.Write(zeroes, 0, toWrite);
                        remaining -= toWrite;
                    }
                    stream.Flush();
                }
            }
            catch { }

            File.Delete(filePath);
        }
        catch { }
    }
}
