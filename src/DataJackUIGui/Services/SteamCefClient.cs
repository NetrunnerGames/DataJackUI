using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataJackUIGui.Services;

/// <summary>
/// Client for Steam's Chromium Embedded Framework (CEF) Remote Debugging interface.
/// When Steam is launched with -cef-enable-debugging, it exposes the standard Chrome
/// DevTools Protocol (CDP) over a local port (default 8080), allowing safe, non-invasive
/// UI automation and inspection without touching native memory.
/// </summary>
public class SteamCefClient
{
    private static readonly HttpClient s_httpClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    public class CefPage
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Type { get; set; }
        public string? Url { get; set; }
        public string? WebSocketDebuggerUrl { get; set; }
    }

    /// <summary>Checks whether Steam's CEF remote debugging port is open and responding.</summary>
    public async Task<bool> IsCefDebuggingAvailableAsync(int port = 8080)
    {
        try
        {
            string url = $"http://127.0.0.1:{port}/json";
            using var response = await s_httpClient.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Gets the list of available CEF debugging pages/tabs from the Steam client.</summary>
    public async Task<CefPage[]?> GetPagesAsync(int port = 8080)
    {
        try
        {
            string url = $"http://127.0.0.1:{port}/json";
            string json = await s_httpClient.GetStringAsync(url);
            return JsonSerializer.Deserialize<CefPage[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Executes a JavaScript snippet inside Steam's main client web context.
    /// Safe, crash-proof, and survives Steam binary updates.
    /// </summary>
    public async Task<string?> EvaluateScriptAsync(string jsExpression, int port = 8080, int timeoutMs = 3000)
    {
        var pages = await GetPagesAsync(port);
        if (pages is null || pages.Length == 0) return null;

        // Target the main Steam client page (SP / SharedJSContext / Steam)
        CefPage? targetPage = null;
        foreach (var page in pages)
        {
            if (string.IsNullOrEmpty(page.WebSocketDebuggerUrl)) continue;
            if (page.Title?.Contains("Steam", StringComparison.OrdinalIgnoreCase) == true ||
                page.Url?.Contains("steamloopback", StringComparison.OrdinalIgnoreCase) == true)
            {
                targetPage = page;
                break;
            }
        }
        targetPage ??= pages[0];

        if (string.IsNullOrEmpty(targetPage.WebSocketDebuggerUrl)) return null;

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(targetPage.WebSocketDebuggerUrl), cts.Token);

            var cdpRequest = new
            {
                id = 1,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression = jsExpression,
                    returnByValue = true
                }
            };

            string requestJson = JsonSerializer.Serialize(cdpRequest);
            byte[] bytes = Encoding.UTF8.GetBytes(requestJson);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

            byte[] buffer = new byte[8192];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Text)
            {
                string responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                return responseJson;
            }
        }
        catch
        {
            // WebSocket connection or timeout failure
        }

        return null;
    }

    /// <summary>Triggers a soft reload of the Steam client UI without restarting the Steam process.</summary>
    public async Task<bool> ReloadSteamUiAsync(int port = 8080)
    {
        string? result = await EvaluateScriptAsync("window.location.reload();", port);
        return result is not null;
    }
}
