using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DataJackUIGui.Services.GameData;

public class AchievementIconCache
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUI", "achievement-icons");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15.0) };

    private readonly SemaphoreSlim _gate = new(6);

    public async Task<string?> GetAsync(string? url, CancellationToken ct = default)
    {
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.Segments.Length < 2)
        {
            return null;
        }
        string text = uri.Segments[^2].TrimEnd('/');
        string text2 = uri.Segments[^1];
        if (text2.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }
        string path = Path.Combine(Root, text, text2);
        if (File.Exists(path))
        {
            return path;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(path))
            {
                return path;
            }
            byte[] bytes = await _http.GetByteArrayAsync(uri, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
