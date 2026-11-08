using System.IO;
using System.Net.Http;

namespace DataJackUIGui.Services;

/// <summary>
/// Resilient file downloader for AutoUpdater.NET and application downloads that routes GitHub requests
/// through Cloudflare Edge Cache and mirror proxies (<see cref="GithubProxy.Candidates"/>).
/// Prevents throttling, slow downloads, and regional connection drops.
/// </summary>
public static class ProxiedFileDownloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static async Task DownloadFileAsync(
        string url,
        string targetFilePath,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        Exception? lastEx = null;

        foreach (var candidate in GithubProxy.Candidates(url))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await Http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int read;

                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    totalRead += read;
                    if (totalBytes > 0 && progress != null)
                    {
                        progress.Report((int)((totalRead * 100) / totalBytes));
                    }
                }

                return; // Download succeeded
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                PluginLog.Log($"[ProxiedFileDownloader] Failed downloading from {candidate}: {ex.Message}. Trying next candidate...");
                try { if (File.Exists(targetFilePath)) File.Delete(targetFilePath); } catch { }
            }
        }

        throw lastEx ?? new HttpRequestException($"Failed to download {url} from any mirror candidate.");
    }
}
