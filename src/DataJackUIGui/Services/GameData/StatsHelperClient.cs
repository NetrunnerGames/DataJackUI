using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataJackUIGui.Services.GameData;

public class StatsHelperClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30.0);

    public async Task<StatsSaveResult> SaveAsync(long appId, GameStatsEdit edit, CancellationToken ct = default(CancellationToken))
    {
        string? processPath = Environment.ProcessPath;
        if (processPath == null)
        {
            return new StatsSaveResult(StatsSaveOutcome.Failed, "Can't locate application executable.");
        }
        ProcessStartInfo startInfo = new ProcessStartInfo(processPath, $"--stats-helper {appId}")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };
        try
        {
            using Process proc = Process.Start(startInfo) ?? throw new InvalidOperationException("helper didn't start");
            await proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new StatsHelperRequest(new Dictionary<string, bool>(edit.Achievements), new Dictionary<string, double>(edit.Stats))));
            proc.StandardInput.Close();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            string? line;
            try
            {
                line = await proc.StandardOutput.ReadLineAsync(timeout.Token);
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
                return new StatsSaveResult(StatsSaveOutcome.LiveRejected, "Steam didn't answer in time.");
            }
            StatsHelperResponse? statsHelperResponse = ((line == null) ? null : JsonSerializer.Deserialize<StatsHelperResponse>(line));
            return (statsHelperResponse != null && statsHelperResponse.Ok) ? new StatsSaveResult(StatsSaveOutcome.SavedLive) : new StatsSaveResult(StatsSaveOutcome.LiveRejected, statsHelperResponse?.Error ?? "No answer from the helper.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex3)
        {
            return new StatsSaveResult(StatsSaveOutcome.Failed, ex3.Message);
        }
    }
}
