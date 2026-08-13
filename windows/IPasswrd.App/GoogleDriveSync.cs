using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IPasswrd.App;

// Google Drive sync backend (OAuth 2.0 + Drive REST API).
//
// Unlike the folder-sync providers (iCloud Drive), Google keeps the canonical vault LOCAL and
// pushes / pulls the encrypted blob to a single Drive file ("vault.ipvault") over the API. The
// vault is E2E-encrypted before it ever leaves the machine, so Drive only ever holds ciphertext.
//
// Auth: the desktop "loopback" flow with PKCE. We open the system browser to Google's consent
// page with redirect_uri=http://127.0.0.1:{ephemeral-port}; a tiny TcpListener catches the
// authorization code (TcpListener, not HttpListener, so no URL-ACL / admin is needed). We trade
// the code for an access token + refresh token; the refresh token is stored DPAPI-encrypted,
// tied to the Windows account, never synced.
//
// Scope: drive.file — access ONLY to files this app creates. IPasswrd cannot see the rest of the
// user's Drive. openid+email are requested only to show which account is connected.
public sealed class GoogleDriveSync
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";
    private const string DriveFiles = "https://www.googleapis.com/drive/v3/files";
    private const string DriveUpload = "https://www.googleapis.com/upload/drive/v3/files";
    private const string Scope = "https://www.googleapis.com/auth/drive.file openid email";
    private const string FolderMime = "application/vnd.google-apps.folder";
    // Drive file + folder names. Overridable via env (IPASSWRD_GDRIVE_FILE / _FOLDER) for isolated
    // sync tests; production leaves them unset → the real "IPasswrd/vault.ipvault".
    private readonly string _fileName;
    private readonly string _folderName;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // Baked-in OAuth *client id* for the shipped IPasswrd build (created ONCE in the author's Google
    // Cloud project). A client id is not a secret — it is visible in every OAuth redirect — so it is
    // fine to ship. With it present, end users just click "Sign in with Google", no per-user setup.
    //
    // The client SECRET is deliberately NOT in source. Even though a desktop client's secret is not
    // truly confidential under PKCE, committing it to a public repo gets it auto-flagged (and can be
    // auto-revoked) by Google's secret scanner, so it is loaded at runtime instead — from the
    // IPASSWRD_GOOGLE_SECRET environment variable or a local, git-ignored google_oauth.json. If none
    // is present the token exchange is attempted PKCE-only (no secret), which works for OAuth clients
    // created as the "iOS"/"Android"/public type. See SECURITY.md → "Google OAuth".
    private const string EmbeddedClientId = "520945928883-8l31pocdehdrgagp3e8dh0sie6ocsjol.apps.googleusercontent.com";

    private readonly string _dir;               // where token + config live (per-device, not synced)
    private string _clientId = "";
    private string _clientSecret = "";
    private string? _refreshToken;
    private string _accessToken = "";
    private DateTimeOffset _accessExpiry = DateTimeOffset.MinValue;

    private string? _fileId;                     // cached Drive file id of the vault
    private string? _fileParent;                 // current parent folder id of the vault file (for migration)
    private string? _folderId;                   // cached id of the "IPasswrd" folder
    public string? LastModifiedTime { get; private set; }   // RFC3339 mtime of the copy we last saw on Drive
    public string? Email { get; private set; }

    public GoogleDriveSync(string dataDir)
    {
        _dir = dataDir;
        _folderName = Environment.GetEnvironmentVariable("IPASSWRD_GDRIVE_FOLDER") is { Length: > 0 } gf ? gf : "IPasswrd";
        _fileName = Environment.GetEnvironmentVariable("IPASSWRD_GDRIVE_FILE") is { Length: > 0 } gn ? gn : "vault.ipvault";
        (_clientId, _clientSecret) = LoadConfig(dataDir);
        if (_clientId.Length == 0) _clientId = EmbeddedClientId;   // no override → shipped client id
        if (_clientSecret.Length == 0)                              // secret is never baked into source
            _clientSecret = Environment.GetEnvironmentVariable("IPASSWRD_GOOGLE_SECRET")?.Trim() ?? "";
        _refreshToken = LoadToken(dataDir);
    }

    // A client id is enough to start the PKCE flow; the secret is optional (see the ctor).
    public bool IsConfigured => _clientId.Length > 0;
    public bool IsSignedIn => !string.IsNullOrEmpty(_refreshToken);

    // ---- app OAuth-client config (Client ID / Secret the user pastes in from their Google Cloud project) ----

    private static string ConfigPath(string dir) => Path.Combine(dir, "google_oauth.json");
    private static string TokenPath(string dir) => Path.Combine(dir, "gdrive_token.bin");

    private sealed class OAuthConfig { public string? client_id { get; set; } public string? client_secret { get; set; } }

    public static (string id, string secret) LoadConfig(string dir)
    {
        // Порядок: файл в папке данных (личная настройка) выигрывает у файла, приехавшего рядом
        // с приложением в релизном пакете. В репозиторий секрет по-прежнему не попадает
        // (google_oauth.json игнорируется git'ом) — он кладётся в пакет на сборке; для
        // installed-приложений Google не считает такой секрет конфиденциальным.
        (string id, string secret) fromData = ReadConfigFile(ConfigPath(dir));
        if (fromData.id.Length > 0 || fromData.secret.Length > 0) return fromData;
        return ReadConfigFile(Path.Combine(AppContext.BaseDirectory, "google_oauth.json"));
    }

    private static (string id, string secret) ReadConfigFile(string p)
    {
        try
        {
            if (!File.Exists(p)) return ("", "");
            var c = JsonSerializer.Deserialize<OAuthConfig>(File.ReadAllText(p));
            return (c?.client_id?.Trim() ?? "", c?.client_secret?.Trim() ?? "");
        }
        catch { return ("", ""); }
    }

    public void SaveConfig(string clientId, string clientSecret)
    {
        _clientId = clientId.Trim();
        _clientSecret = clientSecret.Trim();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath(_dir),
            JsonSerializer.Serialize(new OAuthConfig { client_id = _clientId, client_secret = _clientSecret },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---- refresh-token storage (DPAPI, per Windows account) ----

    private static string? LoadToken(string dir)
    {
        try
        {
            string p = TokenPath(dir);
            if (!File.Exists(p)) return null;
            byte[] dec = ProtectedData.Unprotect(File.ReadAllBytes(p), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return null; }
    }

    private void StoreToken(string? refreshToken)
    {
        _refreshToken = refreshToken;
        try
        {
            if (string.IsNullOrEmpty(refreshToken)) { if (File.Exists(TokenPath(_dir))) File.Delete(TokenPath(_dir)); return; }
            Directory.CreateDirectory(_dir);
            byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenPath(_dir), enc);
        }
        catch { /* best effort — worst case the user signs in again */ }
    }

    // ---- PKCE helpers ----

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (string verifier, string challenge) Pkce()
    {
        byte[] v = RandomNumberGenerator.GetBytes(32);
        string verifier = B64Url(v);
        string challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    // ---- sign-in (loopback + PKCE) ----

    /// <summary>Run the browser consent flow and obtain a refresh token. Returns the connected email.
    /// Throws on failure (user closed the tab, denied consent, bad client config, etc.).</summary>
    public async Task<string?> SignInAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("google_client_not_configured");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string redirect = $"http://127.0.0.1:{port}/";

        var (verifier, challenge) = Pkce();
        string state = B64Url(RandomNumberGenerator.GetBytes(16));
        string authUrl = AuthEndpoint +
            "?client_id=" + Uri.EscapeDataString(_clientId) +
            "&redirect_uri=" + Uri.EscapeDataString(redirect) +
            "&response_type=code" +
            "&scope=" + Uri.EscapeDataString(Scope) +
            "&code_challenge=" + challenge +
            "&code_challenge_method=S256" +
            "&access_type=offline&prompt=consent" +
            "&state=" + state;

        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(authUrl) { UseShellExecute = true }); }
        catch { listener.Stop(); throw; }

        string? code = null, error = null, gotState = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            // Keep accepting until a request actually carries ?code (or ?error). Chrome opens a
            // speculative/preconnect socket and fetches /favicon.ico around the redirect — those must
            // NOT end the wait, or the real redirect arrives after the listener has already closed.
            while (code is null && error is null)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(link.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                string? requestLine = await reader.ReadLineAsync();      // "GET /?code=...&state=... HTTP/1.1"
                if (string.IsNullOrEmpty(requestLine)) continue;         // empty preconnect — keep listening

                int s = requestLine.IndexOf(' ');
                int e = requestLine.IndexOf(' ', s + 1);
                string target = (s >= 0 && e > s) ? requestLine.Substring(s + 1, e - s - 1) : requestLine;
                int q = target.IndexOf('?');
                if (q >= 0)
                {
                    foreach (var kv in target[(q + 1)..].Split('&'))
                    {
                        int eq = kv.IndexOf('=');
                        if (eq < 0) continue;
                        string k = kv[..eq], val = Uri.UnescapeDataString(kv[(eq + 1)..]);
                        if (k == "code") code = val; else if (k == "error") error = val; else if (k == "state") gotState = val;
                    }
                }

                bool done = code is not null || error is not null;
                string body = done && error is null
                    ? "<h2>IPasswrd подключён к Google.</h2><p>Можно закрыть эту вкладку и вернуться в приложение.</p>"
                    : done ? "<h2>Не удалось подключить Google.</h2><p>Вернитесь в приложение и попробуйте ещё раз.</p>"
                    : "<h2>IPasswrd</h2><p>Ожидание входа…</p>";        // code-less probe (favicon/preconnect)
                byte[] resp = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\nContent-Length: " +
                    Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);
                await stream.WriteAsync(resp, 0, resp.Length, CancellationToken.None);
                await stream.FlushAsync(CancellationToken.None);
            }
        }
        finally { try { listener.Stop(); } catch { } }

        if (error is not null) throw new InvalidOperationException("consent_" + error);
        if (code is null || gotState != state) throw new InvalidOperationException("no_code");

        // exchange the code for tokens
        var form = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _clientId,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,   // PKCE — this, not the secret, is what proves the client
        };
        if (_clientSecret.Length > 0) form["client_secret"] = _clientSecret;
        using var tokResp = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        string tokJson = await tokResp.Content.ReadAsStringAsync(ct);
        if (!tokResp.IsSuccessStatusCode) throw new InvalidOperationException("token_exchange_failed");
        using var doc = JsonDocument.Parse(tokJson);
        var root = doc.RootElement;
        _accessToken = root.GetProperty("access_token").GetString() ?? "";
        _accessExpiry = DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 3000);
        if (root.TryGetProperty("refresh_token", out var rt)) StoreToken(rt.GetString());
        if (string.IsNullOrEmpty(_refreshToken)) throw new InvalidOperationException("no_refresh_token");

        try { Email = await FetchEmailAsync(ct); } catch { /* display-only */ }
        return Email;
    }

    private async Task<string> EnsureAccessTokenAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTimeOffset.UtcNow < _accessExpiry.AddSeconds(-60))
            return _accessToken;
        if (string.IsNullOrEmpty(_refreshToken)) throw new InvalidOperationException("not_signed_in");

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["refresh_token"] = _refreshToken!,
            ["grant_type"] = "refresh_token",
        };
        if (_clientSecret.Length > 0) form["client_secret"] = _clientSecret;
        using var resp = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // refresh token revoked / expired → force a fresh sign-in
            if ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 401) StoreToken(null);
            throw new InvalidOperationException("refresh_failed");
        }
        using var doc = JsonDocument.Parse(json);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
        _accessExpiry = DateTimeOffset.UtcNow.AddSeconds(doc.RootElement.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 3000);
        return _accessToken;
    }

    private async Task<string?> FetchEmailAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await EnsureAccessTokenAsync(ct));
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
    }

    // ---- Drive file operations ----

    private async Task<HttpResponseMessage> SendAuthed(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await EnsureAccessTokenAsync(ct));
        return await Http.SendAsync(req, ct);
    }

    /// <summary>Find-or-create the "IPasswrd" folder on Drive, caching its id.</summary>
    private async Task<string> EnsureFolderAsync(CancellationToken ct)
    {
        if (_folderId is not null) return _folderId;

        string q = $"mimeType = '{FolderMime}' and name = '{_folderName}' and trashed = false";
        string url = DriveFiles + "?spaces=drive&pageSize=10&fields=" + Uri.EscapeDataString("files(id)") + "&q=" + Uri.EscapeDataString(q);
        using (var req = new HttpRequestMessage(HttpMethod.Get, url))
        using (var resp = await SendAuthed(req, ct))
        {
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
                { _folderId = files[0].GetProperty("id").GetString(); return _folderId!; }
            }
        }
        // none yet — create it
        var meta = new StringContent("{\"name\":\"" + _folderName + "\",\"mimeType\":\"" + FolderMime + "\"}", Encoding.UTF8);
        meta.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using (var req = new HttpRequestMessage(HttpMethod.Post, DriveFiles + "?fields=id") { Content = meta })
        using (var resp = await SendAuthed(req, ct))
        {
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("drive_folder_failed");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            _folderId = doc.RootElement.GetProperty("id").GetString();
            return _folderId!;
        }
    }

    /// <summary>Locate our vault file on Drive, caching its id + parent. Returns (id, modifiedTime) or null if none yet.</summary>
    public async Task<(string id, string modifiedTime)?> FindAsync(CancellationToken ct = default)
    {
        string url = DriveFiles +
            "?spaces=drive&pageSize=10&fields=" + Uri.EscapeDataString("files(id,modifiedTime,name,parents)") +
            "&q=" + Uri.EscapeDataString($"name = '{_fileName}' and trashed = false");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await SendAuthed(req, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("drive_list_failed");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.GetArrayLength() == 0) return null;
        var f = files[0];
        _fileId = f.GetProperty("id").GetString();
        _fileParent = f.TryGetProperty("parents", out var ps) && ps.GetArrayLength() > 0 ? ps[0].GetString() : null;
        string mt = f.TryGetProperty("modifiedTime", out var m) ? (m.GetString() ?? "") : "";
        return (_fileId!, mt);
    }

    /// <summary>Cheap change-probe: the current modifiedTime of the remote vault (null if it doesn't exist).</summary>
    public async Task<string?> RemoteModifiedTimeAsync(CancellationToken ct = default)
    {
        if (_fileId is null) { var f = await FindAsync(ct); return f?.modifiedTime; }
        string url = DriveFiles + "/" + _fileId + "?fields=modifiedTime";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await SendAuthed(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) { _fileId = null; return null; }
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("drive_meta_failed");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("modifiedTime", out var m) ? m.GetString() : null;
    }

    /// <summary>Download the remote vault bytes (or null if there is no remote file yet).</summary>
    public async Task<byte[]?> PullAsync(CancellationToken ct = default)
    {
        if (_fileId is null) { var f = await FindAsync(ct); if (f is null) return null; }
        string url = DriveFiles + "/" + _fileId + "?alt=media";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await SendAuthed(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) { _fileId = null; return null; }
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("drive_download_failed");
        // refresh our notion of the remote mtime while we're here
        try { LastModifiedTime = await RemoteModifiedTimeAsync(ct); } catch { /* non-fatal */ }
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Upload bytes as the vault file (create on first push, else replace media). Returns new modifiedTime.</summary>
    public async Task<string?> PushAsync(byte[] bytes, CancellationToken ct = default)
    {
        string folderId = await EnsureFolderAsync(ct);
        if (_fileId is null) { try { await FindAsync(ct); } catch { /* create below */ } }

        HttpResponseMessage resp;
        if (_fileId is null)
        {
            // create inside the IPasswrd folder (metadata + media)
            string boundary = "ipw" + B64Url(RandomNumberGenerator.GetBytes(12));
            var content = new MultipartContent("related", boundary);
            var meta = new StringContent("{\"name\":\"" + _fileName + "\",\"parents\":[\"" + folderId + "\"]}", Encoding.UTF8);
            meta.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Add(meta);
            var media = new ByteArrayContent(bytes);
            media.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Add(media);
            using var req = new HttpRequestMessage(HttpMethod.Post, DriveUpload + "?uploadType=multipart&fields=id,modifiedTime") { Content = content };
            resp = await SendAuthed(req, ct);
            _fileParent = folderId;
        }
        else
        {
            using var req = new HttpRequestMessage(HttpMethod.Patch, DriveUpload + "/" + _fileId + "?uploadType=media&fields=id,modifiedTime")
            { Content = new ByteArrayContent(bytes) };
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            resp = await SendAuthed(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) { resp.Dispose(); _fileId = null; return await PushAsync(bytes, ct); }  // was deleted remotely → recreate

            // migrate a legacy root file into the IPasswrd folder (one-time move)
            if (resp.IsSuccessStatusCode && _fileParent is not null && _fileParent != folderId)
            {
                try
                {
                    string moveUrl = DriveFiles + "/" + _fileId + "?addParents=" + folderId + "&removeParents=" + _fileParent + "&fields=id";
                    using var mv = new HttpRequestMessage(HttpMethod.Patch, moveUrl) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                    using var mvResp = await SendAuthed(mv, ct);
                    if (mvResp.IsSuccessStatusCode) _fileParent = folderId;
                }
                catch { /* non-fatal — file still syncs, just stays where it is */ }
            }
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("drive_upload_failed");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var id)) _fileId = id.GetString();
            LastModifiedTime = root.TryGetProperty("modifiedTime", out var m) ? m.GetString() : LastModifiedTime;
            return LastModifiedTime;
        }
    }

    public void SignOut()
    {
        string? rt = _refreshToken;
        StoreToken(null);
        _accessToken = ""; _accessExpiry = DateTimeOffset.MinValue; _fileId = null; LastModifiedTime = null; Email = null;
        if (!string.IsNullOrEmpty(rt))
            _ = Http.PostAsync(RevokeEndpoint, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = rt! }));
    }

    // ---- маленькие служебные файлы в той же папке (общий буфер обмена и прочее) ----
    // Тот же приём, что с сейфом: find-or-create по имени, id кешируется, 404 сбрасывает кеш.

    private readonly Dictionary<string, string> _smallIds = new(StringComparer.Ordinal);

    private async Task<string?> FindSmallAsync(string name, CancellationToken ct)
    {
        if (_smallIds.TryGetValue(name, out string? cached)) return cached;
        string url = DriveFiles + "?spaces=drive&pageSize=5&fields=" + Uri.EscapeDataString("files(id)") +
            "&q=" + Uri.EscapeDataString($"name = '{name}' and trashed = false");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await SendAuthed(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.GetArrayLength() == 0) return null;
        string? id = files[0].GetProperty("id").GetString();
        if (id is not null) _smallIds[name] = id;
        return id;
    }

    /// <summary>Скачать служебный файл целиком; null — его нет.</summary>
    public async Task<byte[]?> DownloadSmallAsync(string name, CancellationToken ct = default)
    {
        string? id = await FindSmallAsync(name, ct);
        if (id is null) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, DriveFiles + "/" + id + "?alt=media");
        using var resp = await SendAuthed(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) { _smallIds.Remove(name); return null; }
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Залить служебный файл (создать или заменить содержимое).</summary>
    public async Task UploadSmallAsync(string name, byte[] bytes, CancellationToken ct = default)
    {
        string? id = await FindSmallAsync(name, ct);
        if (id is null)
        {
            string folderId = await EnsureFolderAsync(ct);
            string boundary = "ipw" + B64Url(RandomNumberGenerator.GetBytes(12));
            var content = new MultipartContent("related", boundary);
            var meta = new StringContent("{\"name\":\"" + name + "\",\"parents\":[\"" + folderId + "\"]}", Encoding.UTF8);
            meta.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Add(meta);
            var media = new ByteArrayContent(bytes);
            media.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Add(media);
            using var req = new HttpRequestMessage(HttpMethod.Post, DriveUpload + "?uploadType=multipart&fields=id") { Content = content };
            using var resp = await SendAuthed(req, ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("drive_upload_failed");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("id", out var nid) && nid.GetString() is { } s) _smallIds[name] = s;
            return;
        }

        using var patch = new HttpRequestMessage(HttpMethod.Patch, DriveUpload + "/" + id + "?uploadType=media&fields=id")
        { Content = new ByteArrayContent(bytes) };
        patch.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var presp = await SendAuthed(patch, ct);
        if (presp.StatusCode == HttpStatusCode.NotFound) { _smallIds.Remove(name); await UploadSmallAsync(name, bytes, ct); return; }
        if (!presp.IsSuccessStatusCode) throw new InvalidOperationException("drive_upload_failed");
    }

    /// <summary>Удалить служебный файл — применённый буфер не должен лежать в облаке.</summary>
    public async Task DeleteSmallAsync(string name, CancellationToken ct = default)
    {
        string? id = await FindSmallAsync(name, ct);
        if (id is null) return;
        using var req = new HttpRequestMessage(HttpMethod.Delete, DriveFiles + "/" + id);
        using var resp = await SendAuthed(req, ct);
        _smallIds.Remove(name);
    }
}
