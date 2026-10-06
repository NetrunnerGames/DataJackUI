using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Web;
using DataJackUIGui.Models;

namespace DataJackUIGui.Services;

public class AuthService
{
    internal static string AuthFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataJackUIGui", "auth.dat");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;

    public string? DisplayName { get; private set; }
    public string? Username { get; private set; }
    public string? UserId { get; private set; }
    public string? Email { get; private set; }
    public string? AvatarUrl { get; private set; }
    public string? DiscordId { get; private set; }

    public UserEntitlement CurrentEntitlement { get; private set; } = new();

    public bool IsBanned => CurrentEntitlement.IsBanned;
    public bool IsAppAllowed(long appid) => CurrentEntitlement.IsAppAllowed(appid);

    /// <summary>True when a real (Discord) account is signed in. Guests have no session.</summary>
    public bool IsSignedIn => _refreshToken is not null;

    /// <summary>True when browsing as a guest (no account signed in).</summary>
    public bool IsGuest => !IsSignedIn;

    /// <summary>True when the signed-in session is a Discord bot placeholder account
    /// (email @bot.lua.tools) rather than a full linked lua.tools account.</summary>
    public bool IsBotProvisioned =>
        IsSignedIn && Email?.EndsWith(AppConfig.BotAccountEmailDomain, StringComparison.OrdinalIgnoreCase) == true;

    public event Action? AuthStateChanged;

    // ── Session restore ─────────────────────────────────────────────

    /// <summary>
    /// Restore a persisted session if one exists. Guests have no session and simply
    /// browse with the public endpoints. Returns true when an account is signed in.
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        StoredAuth? stored = LoadStored();
        if (stored is null || string.IsNullOrEmpty(stored.RefreshToken)) return false;

        _accessToken = stored.AccessToken;
        _refreshToken = stored.RefreshToken;
        _expiresAt = stored.ExpiresAt;
        Username = stored.Username;
        DisplayName = stored.Username;
        UserId = stored.UserId;
        AvatarUrl = stored.AvatarUrl;
        Email = null;
        DiscordId = null;

        // Immediately sync UI with restored cached credentials
        NotifyAuthStateChanged();

        // Token still comfortably valid, or refresh succeeds → keep the session
        if (_expiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            bool inServer = await VerifyServerMembershipAsync();
            if (!inServer) return false;
            NotifyAuthStateChanged();
            return true;
        }
        try
        {
            await RefreshAsync();
            bool inServer = await VerifyServerMembershipAsync();
            if (!inServer) return false;
            NotifyAuthStateChanged();
            return true;
        }
        catch
        {
            ClearSession(); // revoked/expired. Fall back to guest
            NotifyAuthStateChanged();
            return false;
        }
    }

    /// <summary>
    /// Check if the numeric Discord User ID matches the expected user, and if they are a member of the required server.
    private string? _cachedClientId;

    public async Task<string> GetDiscordClientIdAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_cachedClientId)) return _cachedClientId;
        try
        {
            var res = await _http.GetAsync($"{AppConfig.DiscordAuthWorkerUrl.Replace("/api/login", "")}/api/config", ct);
            if (res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("client_id", out var idProp))
                {
                    _cachedClientId = idProp.GetString();
                    if (!string.IsNullOrEmpty(_cachedClientId)) return _cachedClientId;
                }
            }
        }
        catch { /* best effort fallback */ }
        return AppConfig.DiscordClientId;
    }

    /// <summary>
    /// Check server membership and user identity via Cloudflare Auth Worker Edge.
    /// If the numeric ID changed or the user left the required server, logs them out automatically.
    /// </summary>
    public async Task<bool> VerifyServerMembershipAsync(CancellationToken ct = default)
    {
        if (!IsSignedIn || string.IsNullOrEmpty(_accessToken))
            return false;

        try
        {
            var verifyReq = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.DiscordAuthWorkerUrl.Replace("/api/login", "")}/api/verify")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { access_token = _accessToken, discord_id = DiscordId }),
                    Encoding.UTF8, "application/json")
            };

            var res = await _http.SendAsync(verifyReq, ct);
            if (!res.IsSuccessStatusCode)
            {
                SignOut();
                return false;
            }

            string json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool valid = root.TryGetProperty("valid", out var validProp) && validProp.GetBoolean();
            if (!valid)
            {
                SignOut();
                return false;
            }

            if (root.TryGetProperty("user_id", out var userIdProp))
            {
                DiscordId = userIdProp.GetString();
            }

            return true;
        }
        catch
        {
            // Network glitch: preserve session
            return true;
        }
    }

    // ── Interactive sign-in ─────────────────────────────────────────

    /// <summary>
    /// Native Discord Desktop IPC sign-in.
    /// </summary>
    public async Task SignInAsync(CancellationToken ct = default)
    {
        // 1. Get the raw OAuth code from the local Discord Desktop Client via IPC
        string code = await GetDiscordIpcCodeAsync(ct);

        // 2. Exchange code with Worker for session
        await ExchangeCodeForSessionAsync(code, ct);
    }

    /// <summary>
    /// Exchange an OAuth authorization code (from Discord IPC or datajackui:// protocol URL) for a session via Cloudflare Auth Worker.
    /// </summary>
    public async Task ExchangeCodeForSessionAsync(string code, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, AppConfig.DiscordAuthWorkerUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { code }),
                Encoding.UTF8, "application/json"),
        };

        var res = await _http.SendAsync(req, ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new AuthException($"Auth exchange failed: {body}");

        var session = JsonSerializer.Deserialize<SupabaseSession>(body)
               ?? throw new AuthException("Invalid session returned from auth worker.");

        ApplySession(session);
        NotifyAuthStateChanged();
    }

    private async Task<string> GetDiscordIpcCodeAsync(CancellationToken ct)
    {
        string clientId = await GetDiscordClientIdAsync(ct);
        if (string.IsNullOrEmpty(clientId)) clientId = "1553794813411721216";

        NamedPipeClientStream? pipe = null;
        for (int i = 0; i < 10; i++)
        {
            var p = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await p.ConnectAsync(1000, ct);
                pipe = p;
                break;
            }
            catch
            {
                p.Dispose();
            }
        }

        if (pipe is null)
            throw new AuthException("Could not connect to Discord. Is the Discord desktop app running?");

        using (pipe)
        {
            try
            {
                // 1. Send Handshake (Opcode 0)
                await SendIpcMessageAsync(pipe, 0, JsonSerializer.Serialize(new { v = 1, client_id = clientId }), ct);

                // Read Handshake response (READY)
                await ReadIpcMessageAsync(pipe, ct);

                // 2. Send Authorize (Opcode 1)
                var authorizePayload = JsonSerializer.Serialize(new
                {
                    cmd = "AUTHORIZE",
                    args = new { client_id = clientId, scopes = new[] { "identify", "email" }, redirect_uri = AppConfig.OAuthCallbackUrl },
                    nonce = Guid.NewGuid().ToString()
                });

                await SendIpcMessageAsync(pipe, 1, authorizePayload, ct);

                // 3. Read Authorize response
                while (true)
                {
                    var response = await ReadIpcMessageAsync(pipe, ct);
                    using var doc = JsonDocument.Parse(response.Payload);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("cmd", out var cmd) && cmd.GetString() == "AUTHORIZE")
                    {
                        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("code", out var codeElement))
                        {
                            return codeElement.GetString()!;
                        }

                        if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
                        {
                            var msg = root.TryGetProperty("data", out var errData) && errData.TryGetProperty("message", out var errMsg)
                                ? errMsg.GetString() : "Unknown error";
                            throw new AuthException($"Discord Auth Denied: {msg}");
                        }
                    }
                }
            }
            catch (EndOfStreamException)
            {
                throw new AuthException("Discord closed the authorization pipe. Please ensure Discord is running and try again.");
            }
            catch (IOException ex)
            {
                throw new AuthException($"Discord pipe communication error: {ex.Message}");
            }
        }
    }

    private static async Task SendIpcMessageAsync(NamedPipeClientStream pipe, int opcode, string payload, CancellationToken ct)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] buffer = new byte[8 + payloadBytes.Length];

        BitConverter.GetBytes(opcode).CopyTo(buffer, 0);
        BitConverter.GetBytes(payloadBytes.Length).CopyTo(buffer, 4);
        payloadBytes.CopyTo(buffer, 8);

        await pipe.WriteAsync(buffer, ct);
    }

    private static async Task<(int Opcode, string Payload)> ReadIpcMessageAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        byte[] header = new byte[8];
        int read = await pipe.ReadAtLeastAsync(header, 8, false, ct);
        if (read < 8) throw new EndOfStreamException();

        int opcode = BitConverter.ToInt32(header, 0);
        int length = BitConverter.ToInt32(header, 4);

        byte[] payloadBytes = new byte[length];
        read = await pipe.ReadAtLeastAsync(payloadBytes, length, false, ct);
        if (read < length) throw new EndOfStreamException();

        return (opcode, Encoding.UTF8.GetString(payloadBytes));
    }


    private static async Task<string> WaitForCallbackAsync(HttpListener listener, CancellationToken ct)
    {
        // 5 minute window for the user to complete the browser flow
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        while (true)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new AuthException(Resources.Strings.Auth_Err_Timeout);
            }

            string? code = HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "").Get("code");
            string? error = HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "").Get("error_description");

            if (code is null && error is null)
            {
                // Favicon or stray request: ignore and keep waiting
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }

            bool ok = code is not null;
            byte[] page = Encoding.UTF8.GetBytes(ResultPage(ok, error));
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = page.Length;
            await ctx.Response.OutputStream.WriteAsync(page, timeout.Token);
            ctx.Response.Close();

            if (!ok) throw new AuthException(error ?? "Sign-in was denied.");
            return code!;
        }
    }

    private async Task<SupabaseSession> ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.SupabaseUrl}/auth/v1/token?grant_type=pkce")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { auth_code = code, code_verifier = verifier }),
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("apikey", AppConfig.SupabaseAnonKey);

        var res = await _http.SendAsync(req, ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new AuthException(string.Format(Resources.Strings.Auth_Err_TokenExchangeFailed, (int)res.StatusCode, body));

        return JsonSerializer.Deserialize<SupabaseSession>(body)
               ?? throw new AuthException(Resources.Strings.Auth_Err_TokenExchangeEmpty);
    }

    // ── Token access / refresh ──────────────────────────────────────

    /// <summary>Returns a valid access token, refreshing first when close to expiry. Throws for guests.</summary>
    public async Task<string> GetValidAccessTokenAsync()
    {
        if (_refreshToken is null) throw new AuthException(Resources.Strings.Auth_Err_NotSignedIn);

        if (_expiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            await _refreshLock.WaitAsync();
            try
            {
                if (_expiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
                    await RefreshAsync();
            }
            finally
            {
                _refreshLock.Release();
            }
        }
        return _accessToken!;
    }

    private async Task RefreshAsync()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.SupabaseUrl}/auth/v1/token?grant_type=refresh_token")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { refresh_token = _refreshToken }),
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("apikey", AppConfig.SupabaseAnonKey);

        var res = await _http.SendAsync(req);
        string body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new AuthException(string.Format(Resources.Strings.Auth_Err_RefreshFailed, (int)res.StatusCode));

        var session = JsonSerializer.Deserialize<SupabaseSession>(body)
                      ?? throw new AuthException(Resources.Strings.Auth_Err_RefreshEmpty);
        ApplySession(session);
    }

    /// <summary>Sign out of the account and return to guest browsing (app stays usable).</summary>
    public void SignOut()
    {
        ClearSession();
        NotifyAuthStateChanged();
    }

    public void NotifyAuthStateChanged()
    {
        if (AuthStateChanged is null) return;
        foreach (var handler in AuthStateChanged.GetInvocationList())
        {
            try
            {
                if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                {
                    dispatcher.Invoke(() => handler.DynamicInvoke());
                }
                else
                {
                    handler.DynamicInvoke();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AuthService] AuthStateChanged error: {ex}");
            }
        }
    }

    // ── Internals ───────────────────────────────────────────────────

    private void ApplySession(SupabaseSession session)
    {
        _accessToken = session.AccessToken;
        _refreshToken = session.RefreshToken;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresIn);

        if (session.User is not null)
        {
            var meta = session.User.Metadata;
            DisplayName = meta?.CustomClaims?.GlobalName ?? meta?.FullName ?? meta?.Name ?? session.User.Email;
            Username = meta?.UserName ?? meta?.CustomClaims?.PreferredUsername ?? meta?.Name ?? (session.User.Email?.Contains('@') == true ? session.User.Email.Split('@')[0] : session.User.Email);
            UserId = session.User.Id ?? DiscordId ?? "";
            Email = session.User.Email;
            AvatarUrl = meta?.AvatarUrl;
        }

        SaveStored(new StoredAuth
        {
            RefreshToken = _refreshToken,
            AccessToken = _accessToken,
            ExpiresAt = _expiresAt,
            Username = Username,
            UserId = UserId,
            AvatarUrl = AvatarUrl,
        });
    }

    private void ClearSession()
    {
        _accessToken = null;
        _refreshToken = null;
        _expiresAt = default;
        DisplayName = Username = UserId = Email = AvatarUrl = DiscordId = null;
        try { File.Delete(AuthFile); } catch { /* best effort */ }
    }

    private const byte PayloadVersion = 0x01;
    private static readonly byte[] AppSalt = "DataJackUI.SecureVault.v1"u8.ToArray();
    private static readonly byte[] DpapiInfo = "DataJackUI.DpapiEntropy.v1"u8.ToArray();

    private static byte[] GetMachineKey()
    {
        var sb = new StringBuilder();
        sb.Append(Environment.MachineName).Append('|');
        sb.Append(Environment.UserName).Append('|');
        sb.Append(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).Append('|');
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            sb.Append(key?.GetValue("MachineGuid")?.ToString()).Append('|');
        }
        catch { }
        try
        {
            sb.Append(System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value).Append('|');
        }
        catch { }

        byte[] ikm = Encoding.UTF8.GetBytes(sb.ToString());
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, AppSalt, "DataJackUI.MachineKey"u8.ToArray());
    }

    private static byte[] GetDpapiEntropy()
    {
        byte[] key = GetMachineKey();
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, AppSalt, DpapiInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static StoredAuth? LoadStored()
    {
        try
        {
            if (!File.Exists(AuthFile)) return null;
            byte[] fileBytes = File.ReadAllBytes(AuthFile);
            if (fileBytes.Length == 0) return null;

            byte[]? payload = null;
            byte[] entropy = GetDpapiEntropy();
            bool isLegacy = false;

            try
            {
                payload = ProtectedData.Unprotect(fileBytes, entropy, DataProtectionScope.CurrentUser);
            }
            catch
            {
                // Fall back to legacy plain DPAPI (unprotect with null entropy)
                try
                {
                    payload = ProtectedData.Unprotect(fileBytes, null, DataProtectionScope.CurrentUser);
                    isLegacy = true;
                }
                catch
                {
                    return null;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(entropy);
            }

            if (payload is null || payload.Length == 0) return null;

            if (isLegacy || payload[0] != PayloadVersion)
            {
                // Legacy plain JSON payload from prior versions
                var legacyAuth = JsonSerializer.Deserialize<StoredAuth>(payload);
                if (legacyAuth is not null)
                {
                    // Migrate immediately to machine-bound AES-GCM + DPAPI
                    SaveStored(legacyAuth);
                }
                return legacyAuth;
            }

            // Modern format: [Version (1B)] + [Nonce (12B)] + [Tag (16B)] + [Ciphertext]
            if (payload.Length < 1 + 12 + 16) return null;

            byte[] nonce = payload[1..13];
            byte[] tag = payload[13..29];
            byte[] cipherBytes = payload[29..];
            byte[] plainBytes = new byte[cipherBytes.Length];
            byte[] key = GetMachineKey();

            try
            {
                using (var aesGcm = new AesGcm(key, 16))
                {
                    aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);
                }

                return JsonSerializer.Deserialize<StoredAuth>(plainBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch
        {
            return null;
        }
    }

    internal static void SaveStored(StoredAuth auth)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AuthFile)!);

        byte[] plainBytes = JsonSerializer.SerializeToUtf8Bytes(auth);
        byte[] key = GetMachineKey();
        byte[] entropy = GetDpapiEntropy();

        try
        {
            // 1. Encrypt with AES-256-GCM using machine-derived key
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] tag = new byte[16];
            byte[] cipherBytes = new byte[plainBytes.Length];

            using (var aesGcm = new AesGcm(key, 16))
            {
                aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);
            }

            // Payload structure: [Version (1 byte)] + [Nonce (12 bytes)] + [Tag (16 bytes)] + [Ciphertext]
            byte[] payload = new byte[1 + 12 + 16 + cipherBytes.Length];
            payload[0] = PayloadVersion;
            Buffer.BlockCopy(nonce, 0, payload, 1, 12);
            Buffer.BlockCopy(tag, 0, payload, 1 + 12, 16);
            Buffer.BlockCopy(cipherBytes, 0, payload, 1 + 12 + 16, cipherBytes.Length);

            // 2. Wrap payload with DPAPI using machine entropy
            byte[] enc = ProtectedData.Protect(payload, entropy, DataProtectionScope.CurrentUser);

            // The token file can be momentarily locked (another instance, AV, indexer). A failed
            // write must never break sign-in: the session is already live in memory. Persisting is
            // a convenience for next launch. Retry briefly, then give up silently.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.WriteAllBytes(AuthFile, enc);
                    return;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(150);
                }
                catch
                {
                    return; // locked/denied. Stay signed in for this session, just don't persist
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private static string CreateCodeVerifier()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(48);
        return Base64Url(bytes);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ResultPage(bool ok, string? error) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>DataJackUI</title>
        <style>
          body { background:#0b0b12; color:#e5e7eb; font-family:'Segoe UI',sans-serif;
                 display:flex; align-items:center; justify-content:center; height:100vh; margin:0; }
          .card { text-align:center; padding:2.5rem 3rem; background:#14141c;
                  border:1px solid rgba(255,255,255,.08); border-radius:14px; }
          h1 { font-size:1.3rem; margin:0 0 .5rem; color:{{(ok ? "#a78bfa" : "#f87171")}}; }
          p { color:#9ca3af; font-size:.95rem; margin:0; }
        </style></head>
        <body><div class="card">
          <h1>{{(ok ? "Signed in!" : "Sign-in failed")}}</h1>
          <p>{{(ok ? "You can close this tab and return to DataJackUI." : WebUtility.HtmlEncode(error ?? "Please try again from the app."))}}</p>
        </div></body></html>
        """;
}

public class AuthException(string message) : Exception(message);
