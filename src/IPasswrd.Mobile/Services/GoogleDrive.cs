using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IPasswrd.Mobile.Services;

/// <summary>Синхронизация сейфа через Google Drive прямо с телефона — тот же файл
/// vault.ipvault в папке «IPasswrd» на Drive, что использует Windows-приложение.
/// Вход — системным окном (ASWebAuthenticationSession через MAUI WebAuthenticator),
/// OAuth c PKCE и БЕЗ client secret (клиент типа «iOS»); refresh-токен лежит в Keychain.
/// Логика работы с Drive повторяет ПК (GoogleDriveSync.cs): find-or-create папку,
/// найти файл, скачать (alt=media), залить (multipart create / media patch).</summary>
public static class GoogleDrive
{
    // ⚠ Заполнить после создания OAuth-клиента типа «iOS» в Google Cloud (проект ipasswrd).
    // ClientId выглядит как "520945928883-XXXXXXXX.apps.googleusercontent.com".
    public const string ClientId = "520945928883-chesjggkssgjurt88p5pv0pbvqta0rf1.apps.googleusercontent.com";

    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v3/userinfo";
    private const string DriveFiles = "https://www.googleapis.com/drive/v3/files";
    private const string DriveUpload = "https://www.googleapis.com/upload/drive/v3/files";
    private const string Scope = "https://www.googleapis.com/auth/drive.file openid email";
    private const string FolderMime = "application/vnd.google-apps.folder";

    private const string FileName = "vault.ipvault";
    private const string FolderName = "IPasswrd";
    private const string RefreshKey = "gdrive.refresh";
    private const string EmailPref = "gdrive.email";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static string? _accessToken;
    private static DateTimeOffset _accessExpiry;
    private static string? _folderId;
    private static string? _fileId;
    private static string? _fileParent;

    public static bool IsConfigured => !ClientId.Contains("PLACEHOLDER");

    /// <summary>Подключён ли Google (есть сохранённый refresh-токен).</summary>
    public static bool IsConnected => LoadRefresh() is { Length: > 0 };

    public static string? Email
    {
        get => Preferences.Get(EmailPref, null as string);
        private set { if (value is null) Preferences.Remove(EmailPref); else Preferences.Set(EmailPref, value); }
    }

    /// <summary>Схема редиректа для iOS-клиента: reversed client id.</summary>
    private static string RedirectScheme
    {
        get
        {
            string id = ClientId.Replace(".apps.googleusercontent.com", "");
            return "com.googleusercontent.apps." + id;
        }
    }

    private static string RedirectUri => RedirectScheme + ":/oauth2redirect";

    // ================= токены =================

    private static byte[]? LoadRefresh() => Svc.KeyStore.Load(RefreshKey);

    private static void StoreRefresh(string? token)
    {
        if (string.IsNullOrEmpty(token)) Svc.KeyStore.Delete(RefreshKey);
        else Svc.KeyStore.Save(RefreshKey, Encoding.UTF8.GetBytes(token));
    }

    // ================= вход =================

    /// <summary>Открывает системное окно согласия Google, получает refresh-токен.
    /// Возвращает подключённый email; бросает исключение при ошибке/отмене.</summary>
    public static async Task<string?> SignInAsync()
    {
        if (!IsConfigured) throw new InvalidOperationException("google_client_not_configured");

        var (verifier, challenge) = Pkce();
        string state = B64Url(RandomNumberGenerator.GetBytes(16));
        string authUrl = AuthEndpoint +
            "?client_id=" + Uri.EscapeDataString(ClientId) +
            "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
            "&response_type=code" +
            "&scope=" + Uri.EscapeDataString(Scope) +
            "&code_challenge=" + challenge +
            "&code_challenge_method=S256" +
            "&access_type=offline&prompt=consent" +
            "&state=" + state;

        WebAuthenticatorResult result = await WebAuthenticator.Default.AuthenticateAsync(
            new WebAuthenticatorOptions
            {
                Url = new Uri(authUrl),
                CallbackUrl = new Uri(RedirectUri),
                PrefersEphemeralWebBrowserSession = false,
            });

        result.Properties.TryGetValue("code", out string? code);
        result.Properties.TryGetValue("state", out string? gotState);
        if (result.Properties.TryGetValue("error", out string? err) && !string.IsNullOrEmpty(err))
            throw new InvalidOperationException("consent_" + err);
        if (string.IsNullOrEmpty(code) || gotState != state)
            throw new InvalidOperationException("no_code");

        var form = new Dictionary<string, string>
        {
            ["code"] = code!,
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
        };
        using var tokResp = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form));
        string tokJson = await tokResp.Content.ReadAsStringAsync();
        if (!tokResp.IsSuccessStatusCode) throw new InvalidOperationException("token_exchange_failed");

        using var doc = JsonDocument.Parse(tokJson);
        var root = doc.RootElement;
        _accessToken = root.GetProperty("access_token").GetString() ?? "";
        _accessExpiry = DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 3000);
        if (root.TryGetProperty("refresh_token", out var rt)) StoreRefresh(rt.GetString());
        if (!IsConnected) throw new InvalidOperationException("no_refresh_token");

        try { Email = await FetchEmailAsync(); } catch { /* только для показа */ }
        return Email;
    }

    public static void SignOut()
    {
        StoreRefresh(null);
        Email = null;
        _accessToken = null; _accessExpiry = default;
        _folderId = _fileId = _fileParent = null;
    }

    private static async Task<string> EnsureAccessTokenAsync()
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTimeOffset.UtcNow < _accessExpiry.AddSeconds(-60))
            return _accessToken!;

        byte[]? rt = LoadRefresh();
        if (rt is null || rt.Length == 0) throw new InvalidOperationException("not_signed_in");
        string refresh = Encoding.UTF8.GetString(rt);

        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["refresh_token"] = refresh,
            ["grant_type"] = "refresh_token",
        };
        using var resp = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form));
        string json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode is 400 or 401) StoreRefresh(null);  // отозван → нужен повторный вход
            throw new InvalidOperationException("refresh_failed");
        }
        using var doc = JsonDocument.Parse(json);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
        _accessExpiry = DateTimeOffset.UtcNow.AddSeconds(doc.RootElement.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 3000);
        return _accessToken!;
    }

    private static async Task<string?> FetchEmailAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await EnsureAccessTokenAsync());
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
    }

    private static async Task<HttpResponseMessage> SendAuthed(HttpRequestMessage req)
    {
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await EnsureAccessTokenAsync());
        return await Http.SendAsync(req);
    }

    // ================= Drive =================

    private static async Task<string> EnsureFolderAsync()
    {
        if (_folderId is not null) return _folderId;

        string q = $"mimeType = '{FolderMime}' and name = '{FolderName}' and trashed = false";
        string url = DriveFiles + "?spaces=drive&pageSize=10&fields=" + Uri.EscapeDataString("files(id)") + "&q=" + Uri.EscapeDataString(q);
        using (var req = new HttpRequestMessage(HttpMethod.Get, url))
        using (var resp = await SendAuthed(req))
        {
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
                { _folderId = files[0].GetProperty("id").GetString(); return _folderId!; }
            }
        }
        var meta = new StringContent("{\"name\":\"" + FolderName + "\",\"mimeType\":\"" + FolderMime + "\"}", Encoding.UTF8);
        meta.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using (var req = new HttpRequestMessage(HttpMethod.Post, DriveFiles + "?fields=id") { Content = meta })
        using (var resp = await SendAuthed(req))
        {
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("drive_folder_failed");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            _folderId = doc.RootElement.GetProperty("id").GetString();
            return _folderId!;
        }
    }

    private static async Task<bool> FindAsync()
    {
        string url = DriveFiles +
            "?spaces=drive&pageSize=10&fields=" + Uri.EscapeDataString("files(id,modifiedTime,name,parents)") +
            "&q=" + Uri.EscapeDataString($"name = '{FileName}' and trashed = false");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await SendAuthed(req);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("drive_list_failed");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.GetArrayLength() == 0) return false;
        var f = files[0];
        _fileId = f.GetProperty("id").GetString();
        _fileParent = f.TryGetProperty("parents", out var ps) && ps.GetArrayLength() > 0 ? ps[0].GetString() : null;
        return true;
    }

    /// <summary>Скачать сейф с Drive (или null, если его там ещё нет).</summary>
    public static async Task<byte[]?> PullAsync()
    {
        if (_fileId is null) { if (!await FindAsync()) return null; }
        string url = DriveFiles + "/" + _fileId + "?alt=media";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await SendAuthed(req);
        if (resp.StatusCode == HttpStatusCode.NotFound) { _fileId = null; return null; }
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("drive_download_failed");
        return await resp.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Залить сейф на Drive (создать при первом разе, иначе заменить содержимое).</summary>
    public static async Task PushAsync(byte[] bytes)
    {
        string folderId = await EnsureFolderAsync();
        if (_fileId is null) { try { await FindAsync(); } catch { /* создадим ниже */ } }

        HttpResponseMessage resp;
        if (_fileId is null)
        {
            string boundary = "ipw" + B64Url(RandomNumberGenerator.GetBytes(12));
            var content = new MultipartContent("related", boundary);
            var meta = new StringContent("{\"name\":\"" + FileName + "\",\"parents\":[\"" + folderId + "\"]}", Encoding.UTF8);
            meta.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Add(meta);
            var media = new ByteArrayContent(bytes);
            media.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Add(media);
            using var req = new HttpRequestMessage(HttpMethod.Post, DriveUpload + "?uploadType=multipart&fields=id,modifiedTime") { Content = content };
            resp = await SendAuthed(req);
            _fileParent = folderId;
        }
        else
        {
            using var req = new HttpRequestMessage(HttpMethod.Patch, DriveUpload + "/" + _fileId + "?uploadType=media&fields=id,modifiedTime")
            { Content = new ByteArrayContent(bytes) };
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            resp = await SendAuthed(req);
            if (resp.StatusCode == HttpStatusCode.NotFound) { resp.Dispose(); _fileId = null; await PushAsync(bytes); return; }

            if (resp.IsSuccessStatusCode && _fileParent is not null && _fileParent != folderId)
            {
                try
                {
                    string moveUrl = DriveFiles + "/" + _fileId + "?addParents=" + folderId + "&removeParents=" + _fileParent + "&fields=id";
                    using var mv = new HttpRequestMessage(HttpMethod.Patch, moveUrl) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                    using var mvResp = await SendAuthed(mv);
                    if (mvResp.IsSuccessStatusCode) _fileParent = folderId;
                }
                catch { /* не критично */ }
            }
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("drive_upload_failed");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("id", out var id)) _fileId = id.GetString();
        }
    }

    // ================= PKCE =================

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (string verifier, string challenge) Pkce()
    {
        byte[] v = RandomNumberGenerator.GetBytes(32);
        string verifier = B64Url(v);
        string challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }
}
