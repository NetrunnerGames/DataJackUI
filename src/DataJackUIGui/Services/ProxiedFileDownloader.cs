using Velopack.Sources;

namespace DataJackUIGui.Services;

/// <summary>
/// An <see cref="IFileDownloader"/> for Velopack's auto-updater that makes its GitHub requests resilient
/// in blocked/throttled regions (e.g. China). Velopack calls this for BOTH the release feed (the API, via
/// DownloadString/DownloadBytes) and the package download (the .nupkg, via DownloadFile). Each call tries
/// the DIRECT GitHub URL first, then falls through the same mirrors as <see cref="GithubProxy"/>
/// (<see cref="GithubProxy.Candidates"/>), so the in-app self-update works where github.com is blocked.
/// Wraps Velopack's stock <see cref="HttpClientFileDownloader"/>, only swapping the URL per attempt.
/// </summary>
public class ProxiedFileDownloader : IFileDownloader
{
    private readonly HttpClientFileDownloader _inner = new();

    public async Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
    {
        Exception? last = null;
        // External communication disabled: Velopack file download commented out
        throw new Exception("External communication disabled.");
        throw last ?? new Exception($"Failed to download {url} from GitHub or any mirror.");
    }

    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        Exception? last = null;
        // External communication disabled: Velopack bytes download commented out
        throw new Exception("External communication disabled.");
        throw last ?? new Exception($"Failed to download {url} from GitHub or any mirror.");
    }

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        Exception? last = null;
        // External communication disabled: Velopack string download commented out
        throw new Exception("External communication disabled.");
        throw last ?? new Exception($"Failed to download {url} from GitHub or any mirror.");
    }
}
