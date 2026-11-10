using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataJackUIGui.Services;

/// <summary>
/// Client for the IceBreaker IPC Named Pipe (\\.\pipe\IceBreaker_IPC).
/// Allows DataJackUI to communicate directly with IceBreaker (version.dll)
/// running inside the Steam process without touching Steam memory or restarting.
/// </summary>
public class IceBreakerIpcClient
{
    private const string PipeName = "IceBreaker_IPC";

    private async Task<string?> SendCommandAsync(string command, int timeoutMs = 1000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);

            byte[] requestBytes = Encoding.UTF8.GetBytes(command.Trim() + "\n");
            await pipe.WriteAsync(requestBytes, cts.Token);
            await pipe.FlushAsync(cts.Token);

            byte[] buffer = new byte[4096];
            int bytesRead = await pipe.ReadAsync(buffer, cts.Token);
            if (bytesRead <= 0) return null;

            return Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
        }
        catch
        {
            // Pipe not available (Steam / IceBreaker not running or busy)
            return null;
        }
    }

    /// <summary>Checks whether IceBreaker is actively running inside Steam and responding to IPC.</summary>
    public async Task<bool> IsAvailableAsync()
    {
        string? res = await SendCommandAsync("PING", 500);
        return res == "PONG";
    }

    /// <summary>Gets the running IceBreaker version string.</summary>
    public async Task<string?> GetVersionAsync()
    {
        return await SendCommandAsync("VERSION", 800);
    }

    /// <summary>Gets the list of currently unlocked AppIDs in IceBreaker memory.</summary>
    public async Task<List<uint>> GetUnlockedAppsAsync()
    {
        string? res = await SendCommandAsync("GET_UNLOCKED", 1500);
        var list = new List<uint>();
        if (string.IsNullOrWhiteSpace(res)) return list;

        string[] parts = res.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (uint.TryParse(part, out uint id))
            {
                list.Add(id);
            }
        }
        return list;
    }

    /// <summary>Dynamically unlocks an AppID in IceBreaker without requiring a Steam restart.</summary>
    public async Task<bool> UnlockAppAsync(uint appId)
    {
        string? res = await SendCommandAsync($"UNLOCK {appId}", 1000);
        return res is not null && res.StartsWith("OK");
    }

    /// <summary>Dynamically locks an AppID in IceBreaker.</summary>
    public async Task<bool> LockAppAsync(uint appId)
    {
        string? res = await SendCommandAsync($"LOCK {appId}", 1000);
        return res is not null && res.StartsWith("OK");
    }

    /// <summary>Tells IceBreaker to re-read all manifests from config/stplug-in.</summary>
    public async Task<bool> ReloadManifestsAsync()
    {
        string? res = await SendCommandAsync("RELOAD", 1500);
        return res is not null && res.StartsWith("OK");
    }
}
