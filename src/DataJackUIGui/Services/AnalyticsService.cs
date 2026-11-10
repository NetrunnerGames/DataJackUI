using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DataJackUIGui.Services;

/// <summary>
/// Anonymous app-launch analytics via Cloudflare Worker -> PostHog.
/// Sends event_name="App Launch" and version="<app_version>".
/// Fire-and-forget: never throws, never blocks startup.
/// </summary>
public class AnalyticsService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion is { } v && v.IndexOf('+') is var i and >= 0 ? v[..i]
        : Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

    /// <summary>Report one app launch. Silent no-op on any failure.</summary>
    public async Task TrackAppLaunchAsync(CancellationToken ct = default)
    {
        try
        {
            var body = new
            {
                event_name = "App Launch",
                version = Version,
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, AppConfig.AnalyticsEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };

            await _http.SendAsync(req, ct);
        }
        catch
        {
            // Telemetry must never affect the app: swallow everything.
        }
    }
}
