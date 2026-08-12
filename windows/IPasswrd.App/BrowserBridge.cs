using Avalonia.Controls;
using Avalonia.Threading;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IPasswrd.Core;

namespace IPasswrd.App;

// Local bridge for the browser extension. The tiny native-messaging host (IPasswrd.Host)
// connects to this named pipe and forwards JSON requests coming from the Chrome extension.
//
// Protocol: one JSON object per line (UTF-8, "\n"), one response line per request.
//   {"cmd":"status"}                                   -> {"ok":true,"unlocked":bool}
//   {"cmd":"credentials","url":"https://…"}            -> {"ok":true,"unlocked":true,"items":[{id,title,username,password,url}]}
//   {"cmd":"save","url":u,"username":l,"password":p,
//    "scope":"base"|"exact"}                           -> {"ok":true,"action":"added"|"updated"|"exists"}
//   {"cmd":"focus"}                                    -> {"ok":true}   (raises the app window / unlock screen)
//
// Security: the pipe is CurrentUserOnly; passwords are only returned while the vault is
// unlocked; nothing is ever logged.
public partial class MainWindow
{
    private const string BridgePipeName = "ipasswrd.browser";
    private CancellationTokenSource? _bridgeCts;
    private string? _bridgeToken;   // per-session bearer for the loopback-HTTP fallback (the named pipe is trusted)
    private string BridgeToken() => _bridgeToken ??= Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

    private void StartBrowserBridge()
    {
        _bridgeCts = new CancellationTokenSource();
        _ = Task.Run(() => BridgeAcceptLoop(_bridgeCts.Token));
        StartHttpBridge();   // loopback-HTTP fallback for when an antivirus blocks the native host (see HttpBridge.cs)
        StartSmsRelay();     // приём СМС-кодов с телефона (Быстрые команды → LAN, см. SmsRelay.cs)
    }

    private async Task BridgeAcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    BridgePipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                var s = server; server = null;                    // handed off to the client task
                _ = Task.Run(() => ServeClient(s, ct), ct);
            }
            catch when (ct.IsCancellationRequested) { server?.Dispose(); return; }
            catch
            {
                server?.Dispose();
                try { await Task.Delay(500, ct); } catch { return; }   // pipe hiccup — retry
            }
        }
    }

    private async Task ServeClient(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var _ = pipe;
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line is null) return;
                string resp = await HandleBridgeRequest(line);
                await writer.WriteLineAsync(resp);
            }
        }
        catch { /* client went away — normal */ }
    }

    private async Task<string> HandleBridgeRequest(string json, bool viaHttp = false)
    {
        bool extFirstSighting = _extLastSeenUtc == default;
        _extLastSeenUtc = DateTime.UtcNow;
        if (extFirstSighting) { try { Avalonia.Threading.Dispatcher.UIThread.Post(() => ExtensionSeen?.Invoke()); } catch { } }   // settings row flips to "connected" on real proof of life
        if (!_extEverConnected) { _extEverConnected = true; try { SaveSettings(); } catch { } try { Avalonia.Threading.Dispatcher.UIThread.Post(RefreshOnboardAfterExtension); } catch { } }   // onboarding: the extension is live — tick its step immediately
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string cmd = root.TryGetProperty("cmd", out var c) ? (c.GetString() ?? "") : "";
            string Get(string name) => root.TryGetProperty(name, out var v) ? (v.GetString() ?? "") : "";

            // The named pipe is CurrentUserOnly, hence trusted. The loopback-HTTP fallback (port 38799)
            // is only Origin-checked, so a second browser extension that forges our Origin could
            // otherwise pump the vault. Secret-returning commands over HTTP therefore require a
            // per-session token, which the extension gets from the trusted pipe's `status` or via a
            // one-time `pair` the user approves in the app window.
            string token = Get("token");
            bool tokenOk = !viaHttp || (_bridgeToken is not null && string.Equals(token, _bridgeToken, StringComparison.Ordinal));
            bool secret = cmd is "credentials" or "save" or "unlock" or "list" or "passkeyList" or "passkeyCreate" or "passkeySign";
            if (viaHttp && secret && !tokenOk) return Resp(new { ok = false, error = "unpaired" });

            switch (cmd)
            {
                case "pair":
                    if (!viaHttp || tokenOk) return Resp(new { ok = true, token = BridgeToken() });
                    return await RequestExtensionPairAsync()
                        ? Resp(new { ok = true, token = BridgeToken() })
                        : Resp(new { ok = false, error = "denied" });
                case "status":
                    return await OnUi(() => viaHttp
                        ? Resp(new { ok = true, unlocked = _vault is not null, paired = tokenOk })
                        : Resp(new { ok = true, unlocked = _vault is not null, token = BridgeToken() }));
                case "credentials":
                {
                    string url = Get("url");
                    return await OnUi(() => BridgeCredentials(url));
                }
                case "save":
                {
                    string url = Get("url"), user = Get("username"), pass = Get("password"), scope = Get("scope");
                    return await OnUi(() => BridgeSave(url, user, pass, scope));
                }
                case "unlock":
                {
                    string pw = Get("password");
                    return await OnUi(() => BridgeUnlock(pw));
                }
                case "list":
                    return await OnUi(BridgeList);
                case "passkeyList":
                {
                    string rpId = Get("rpId");
                    return await OnUi(() => BridgePasskeyList(rpId));
                }
                case "passkeyCreate":
                {
                    string rpId = Get("rpId"), userHandle = Get("userHandle"), userName = Get("userName");
                    return await OnUi(() => BridgePasskeyCreate(rpId, userHandle, userName));
                }
                case "passkeySign":
                {
                    string rpId = Get("rpId"), credId = Get("credId"), data = Get("data");
                    return await OnUi(() => BridgePasskeySign(rpId, credId, data));
                }
                case "focus":
                    return await OnUi(() => { BridgeFocus(); return Resp(new { ok = true }); });
                default:
                    return Resp(new { ok = false, error = "unknown_cmd" });
            }
        }
        catch (Exception ex)
        {
            return Resp(new { ok = false, error = ex.GetType().Name });
        }
    }

    /// <summary>Ask the user, in the app window, to approve a browser-extension pairing over the
    /// loopback-HTTP fallback. Runs on the UI thread; returns false on deny/timeout. The named-pipe
    /// path never needs this — it is CurrentUserOnly and already trusted.</summary>
    private async Task<bool> RequestExtensionPairAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try { BringToFront(); } catch { /* best effort */ }
            var msg = new TextBlock
            {
                Text = Tr("Браузерное расширение просит доступ к сейфу через локальный мост. Разрешайте только если вы сейчас сами подключаете расширение IPasswrd."),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Text2, MaxWidth = 360,
            };
            var res = ShowCard(Tr("Разрешить доступ расширению?"), new Control[] { msg }, Tr("Разрешить"), danger: false);
            if (res is null) return false;
            var (dim, ok) = res.Value;
            var tcs = new TaskCompletionSource<bool>();
            ok.Click += (_, _) => { tcs.TrySetResult(true); CloseCard(dim); };
            dim.DetachedFromVisualTree += (_, _) => tcs.TrySetResult(false);   // cancel / Esc / after allow
            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(45)));
            if (done != tcs.Task) { try { CloseCard(dim); } catch { } tcs.TrySetResult(false); }
            return await tcs.Task;
        });
    }

    private static string Resp(object o) => JsonSerializer.Serialize(o);

    private static async Task<string> OnUi(Func<string> f) => await Dispatcher.UIThread.InvokeAsync(f);

    // All accounts whose registrable domain matches the page, exact address first,
    // then lower-level hosts. Passwords only leave the process while unlocked.
    // Single sign-on hubs log you into a DIFFERENT service, named in a redirect parameter
    // (e.g. id.vk.ru → e.mail.ru). On these curated, trusted hubs we treat the redirect target as
    // the real site (safe to auto-fill). Everywhere else a redirect target is offered in the menu
    // only — an attacker can set redirect_uri, so we never silently auto-fill on their say-so.
    private static readonly HashSet<string> TrustedAuthHubs = new(StringComparer.OrdinalIgnoreCase)
    {
        "id.vk.ru", "id.vk.com", "oauth.vk.com", "connect.vk.com",
        "accounts.google.com", "login.microsoftonline.com", "login.live.com",
        "appleid.apple.com", "passport.yandex.ru", "oauth.yandex.ru",
    };

    private static readonly string[] RedirectKeys =
        { "redirect_uri", "redirect", "redirect_url", "redirecturl", "continue", "return", "return_to",
          "returnto", "return_url", "returnurl", "next", "url", "service", "callback", "retpath" };

    private static string HostOf(string url)
    {
        try { var u = url.Contains("://") ? new Uri(url) : new Uri("https://" + url.Trim()); return u.Host.ToLowerInvariant(); }
        catch { return ""; }
    }

    /// <summary>Registrable domains named by a redirect/continue parameter in an auth URL (the real destination).</summary>
    private static List<string> RedirectTargets(string url)
    {
        var outp = new List<string>();
        try
        {
            var uri = url.Contains("://") ? new Uri(url) : new Uri("https://" + url.Trim());
            if (string.IsNullOrEmpty(uri.Query)) return outp;
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = Uri.UnescapeDataString(part[..eq]);
                if (Array.IndexOf(RedirectKeys, k.ToLowerInvariant()) < 0) continue;
                string v = Uri.UnescapeDataString(part[(eq + 1)..]);
                string dom = Dedup.RegistrableDomain(v);
                if (dom.Length > 0 && dom.Contains('.') && !outp.Contains(dom)) outp.Add(dom);
            }
        }
        catch { /* malformed url */ }
        return outp;
    }

    private string BridgeCredentials(string url)
    {
        // СМС-коды не из сейфа — отдаём их и на заблокированном сейфе.
        if (_vault is null) return Resp(new { ok = true, unlocked = false, items = Array.Empty<object>(), smsCodes = FreshSmsCodes() });
        string baseDom = Dedup.RegistrableDomain(url);
        if (string.IsNullOrEmpty(baseDom)) return Resp(new { ok = true, unlocked = true, items = Array.Empty<object>(), smsCodes = FreshSmsCodes() });
        string nu = NormUrl(url);

        // "family" = domains that count as this page (auto-fillable); "related" = a redirect target on an
        // untrusted page (offered in the menu, never auto-filled).
        var family = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseDom };
        var related = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool trustedHub = TrustedAuthHubs.Contains(HostOf(url));
        foreach (var rd in RedirectTargets(url))
        {
            if (family.Contains(rd)) continue;
            if (trustedHub) family.Add(rd); else related.Add(rd);
        }

        var items = _vault.Items()
            .Where(x => x.Item.Type == "account")
            .Select(x => new { e = x, dom = Dedup.RegistrableDomain(x.Item.Fields.GetValueOrDefault("url", "")) })
            .Where(a => a.dom.Length > 0 && (family.Contains(a.dom) || related.Contains(a.dom)))
            .OrderBy(a => a.dom == baseDom ? 0 : family.Contains(a.dom) ? 1 : 2)          // exact site, then trusted redirect, then menu-only
            .ThenBy(a => NormUrl(a.e.Item.Fields.GetValueOrDefault("url", "")) == nu ? 0 : 1)
            .ThenBy(a => Dedup.HostDepth(a.e.Item.Fields.GetValueOrDefault("url", "")))
            .ThenBy(a => a.e.Item.Fields.GetValueOrDefault("username", ""), StringComparer.OrdinalIgnoreCase)
            .Select(a => new
            {
                id = a.e.Id,
                title = HeaderName(a.e.Item),
                username = a.e.Item.Fields.GetValueOrDefault("username", ""),
                password = a.e.Item.Fields.GetValueOrDefault("password", ""),
                url = a.e.Item.Fields.GetValueOrDefault("url", ""),
                totp = TotpNow(a.e.Item.Fields.GetValueOrDefault("totp", "")),
                related = !family.Contains(a.dom),                                        // menu-only: excluded from silent autofill
            })
            .ToList();
        var codes = _vault.Items()
            .Where(x => x.Item.Type == "totp")
            .Where(x => x.Item.Fields.GetValueOrDefault("totp", "").Trim().Length > 0)
            .Where(x => TotpMatchesSite(x.Item, baseDom))
            .Select(x => new { title = x.Item.Title, code = TotpNow(x.Item.Fields.GetValueOrDefault("totp", "")) })
            .Where(c => !string.IsNullOrEmpty(c.code))
            .ToList();
        return Resp(new { ok = true, unlocked = true, items, codes, smsCodes = FreshSmsCodes() });
    }

    // Standalone authenticator records matched to a site: "google" <-> google.com.
    // Equality of the site base always counts; substring only when both sides are 5+ chars
    // (so "mail" never matches "gmail"). Short-label domains like cs.money are ALSO matched
    // by the whole registrable domain ("csmoney") — their brand is the full name, not "cs".
    private static bool TotpMatchesSite(VaultItem rec, string baseDom)
    {
        static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        bool ip = baseDom.Length > 0 && baseDom.All(ch => char.IsDigit(ch) || ch == '.' || ch == ':');
        int dot = baseDom.IndexOf('.');
        string b = Norm(ip ? baseDom : (dot > 0 ? baseDom.Substring(0, dot) : baseDom));
        if (b.Length == 0) return false;
        string full = ip ? b : Norm(baseDom);   // «cs.money» → «csmoney»

        string issuer = "", account = "";
        try { var cfg = Totp.Parse(rec.Fields.GetValueOrDefault("totp", "")); issuer = cfg.Issuer ?? ""; account = cfg.Account ?? ""; }
        catch { }

        foreach (string cand in new[] { rec.Title, issuer, account })
        {
            string c = Norm(cand);
            if (c.Length == 0) continue;
            if (c == b || c == full) return true;
            if (c.Length >= 5 && b.Length >= 5 && (c.Contains(b) || b.Contains(c))) return true;
            if (full != b && c.Length >= 5 && full.Length >= 5 && (c.Contains(full) || full.Contains(c))) return true;
        }
        return false;
    }

    private static string TotpNow(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return "";
        try { return Totp.GenerateFrom(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }
        catch { return ""; }
    }

    // Cards, documents and personal details for the in-field picker (not site-specific).
    // Only while unlocked.
    private string BridgeList()
    {
        if (_vault is null) return Resp(new { ok = true, unlocked = false, cards = Array.Empty<object>(), docs = Array.Empty<object>(), ids = Array.Empty<object>() });
        var cards = _vault.Items().Where(x => x.Item.Type == "card").Select(x => new
        {
            id = x.Id,
            title = x.Item.Title,
            number = new string(x.Item.Fields.GetValueOrDefault("number", "").Where(char.IsDigit).ToArray()),   // clean digits — site masks reformat themselves
            expiry = x.Item.Fields.GetValueOrDefault("expiry", ""),
            cvc = x.Item.Fields.GetValueOrDefault("cvc", ""),
            holder = x.Item.Fields.GetValueOrDefault("holder", ""),
        }).ToList();
        var docs = _vault.Items().Where(x => x.Item.Type == "doc").Select(x => new
        {
            id = x.Id,
            title = x.Item.Title,
            number = x.Item.Fields.GetValueOrDefault("number", ""),
            issued = x.Item.Fields.GetValueOrDefault("issued", ""),
        }).ToList();
        var ids = _vault.Items().Where(x => x.Item.Type == "identity").Select(x => new
        {
            id = x.Id,
            title = x.Item.Title,
            lastName = x.Item.Fields.GetValueOrDefault("lastName", ""),
            firstName = x.Item.Fields.GetValueOrDefault("firstName", ""),
            middleName = x.Item.Fields.GetValueOrDefault("middleName", ""),
            phone = x.Item.Fields.GetValueOrDefault("phone", ""),
            email = x.Item.Fields.GetValueOrDefault("email", ""),
            zip = x.Item.Fields.GetValueOrDefault("zip", ""),
            country = x.Item.Fields.GetValueOrDefault("country", ""),
            city = x.Item.Fields.GetValueOrDefault("city", ""),
            street = x.Item.Fields.GetValueOrDefault("street", ""),
        }).ToList();
        return Resp(new { ok = true, unlocked = true, cards, docs, ids });
    }

    // Unlock the vault with the master password typed in the BROWSER (extension popup /
    // in-page form). Same lockout bookkeeping as the in-app unlock screen — no brute-force
    // bypass; the password only travels bg -> host -> CurrentUserOnly pipe, never logged.
    private string BridgeUnlock(string password)
    {
        if (_vault is not null) return Resp(new { ok = true, already = true });
        if (Creating) return Resp(new { ok = false, error = "no_vault" });
        if (IsLocked)
        {
            TimeSpan left0 = _lockedUntil - DateTimeOffset.UtcNow;
            if (left0 < TimeSpan.Zero) left0 = TimeSpan.Zero;
            return Resp(new { ok = false, error = "locked_out", wait = FormatSpan(left0) });
        }
        if (string.IsNullOrEmpty(password)) return Resp(new { ok = false, error = "empty_password" });

        try
        {
            _vault = Vault.Unlock(System.IO.File.ReadAllBytes(VaultPath()), password);
        }
        catch (WrongMasterPasswordException)
        {
            _fails++;
            TimeSpan pen = Lockout.PenaltyFor(_fails);
            if (pen > TimeSpan.Zero)
            {
                _lockedUntil = DateTimeOffset.UtcNow + pen;
                SaveLockout();
                if (UnlockScreen.IsVisible) { MasterBox.Text = ""; StartLockCountdown(); }
                TimeSpan left = _lockedUntil - DateTimeOffset.UtcNow;
                return Resp(new { ok = false, error = "locked_out", wait = FormatSpan(left) });
            }
            SaveLockout();
            return Resp(new { ok = false, error = "wrong_password", attemptsLeft = Lockout.AttemptsLeft(_fails) });
        }
        catch (Exception ex) { return Resp(new { ok = false, error = ex.GetType().Name }); }

        ResetLockout();
        EnterVault();
        _ = ArmQuickUnlockAsync();   // arm quick unlock (may ask for one Hello gesture); never blocks
        return Resp(new { ok = true });
    }

    // Save from the browser bubble. scope "base" stores the clean 2nd-level domain,
    // "exact" keeps the address as-is. Same site + same login + new password -> update.
    private string BridgeSave(string url, string username, string password, string scope)
    {
        if (_vault is null) return Resp(new { ok = false, error = "locked" });
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrEmpty(password))
            return Resp(new { ok = false, error = "bad_request" });

        string baseDom = Dedup.RegistrableDomain(url);
        string saveUrl = scope == "exact" ? url.Trim() : "https://" + baseDom;
        string login = (username ?? "").Trim();

        var existing = _vault.Items().FirstOrDefault(x =>
            x.Item.Type == "account" &&
            Dedup.RegistrableDomain(x.Item.Fields.GetValueOrDefault("url", "")) == baseDom &&
            string.Equals(x.Item.Fields.GetValueOrDefault("username", "").Trim(), login,
                StringComparison.OrdinalIgnoreCase));

        string action, id;
        if (existing is not null)
        {
            if (existing.Item.Fields.GetValueOrDefault("password", "") == password)
                return Resp(new { ok = true, action = "exists", id = existing.Id });
            var upd = existing.Item;
            upd.Fields["password"] = password;                        // password change → update in place
            _vault.Update(existing.Id, upd);
            id = existing.Id; action = "updated";
        }
        else
        {
            var item = new VaultItem { Type = "account", Title = DeriveTitle(saveUrl, login) };
            item.Fields["url"] = saveUrl;
            if (login.Length > 0) item.Fields["username"] = login;
            item.Fields["password"] = password;
            id = _vault.Add(item); action = "added";
        }

        Save();
        if (VaultScreen.IsVisible)
        {
            LoadEntries(selectFirst: false);
            RenderSidebar();
        }
        return Resp(new { ok = true, action, id });
    }

    // ---- passkeys (WebAuthn) ----
    // The extension's page shim does all the crypto in WebCrypto; the vault is only the
    // encrypted, synced store for the private key (JWK) + credential metadata. type "passkey".

    private string BridgePasskeyList(string rpId)
    {
        if (_vault is null) return Resp(new { ok = true, unlocked = false, items = Array.Empty<object>() });
        if (string.IsNullOrWhiteSpace(rpId)) return Resp(new { ok = true, unlocked = true, items = Array.Empty<object>() });

        var items = _vault.Items()
            .Where(x => x.Item.Type == "passkey"
                        && string.Equals(x.Item.Fields.GetValueOrDefault("rpId", ""), rpId, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(x.Item.Fields.GetValueOrDefault("credId", ""))
                        && !string.IsNullOrEmpty(x.Item.Fields.GetValueOrDefault("privJwk", "")))
            .Select(x => new
            {
                id = x.Id,
                credId = x.Item.Fields.GetValueOrDefault("credId", ""),
                userHandle = x.Item.Fields.GetValueOrDefault("userHandle", ""),
                userName = x.Item.Fields.GetValueOrDefault("username", ""),
                // privJwk is deliberately NOT returned: the private key never leaves this process.
                // Signing happens in BridgePasskeySign; returning the key here would expose it to page JS.
            })
            .ToList();
        return Resp(new { ok = true, unlocked = true, items });
    }

    // Registration: the APP generates the P-256 keypair and stores the private key. Only the
    // credential id and the PUBLIC key ever leave this process — the private key is never handed to
    // page JS, so a page (or XSS on it) cannot exfiltrate it. Replaces the old page-provides-privJwk path.
    private string BridgePasskeyCreate(string rpId, string userHandle, string userName)
    {
        if (_vault is null) return Resp(new { ok = false, error = "locked" });
        if (string.IsNullOrWhiteSpace(rpId)) return Resp(new { ok = false, error = "bad_request" });

        byte[] credId = RandomNumberGenerator.GetBytes(16);
        byte[] spki;
        string x, y, jwk;
        using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var p = ec.ExportParameters(includePrivateParameters: true);
            x = B64Url(p.Q.X!); y = B64Url(p.Q.Y!);
            jwk = JsonSerializer.Serialize(new { kty = "EC", crv = "P-256", d = B64Url(p.D!), x, y });
            spki = ec.ExportSubjectPublicKeyInfo();
        }

        var item = new VaultItem { Type = "passkey", Title = rpId };
        item.Fields["rpId"] = rpId;
        item.Fields["url"] = rpId;                       // detail view shows this as "Сайт"
        if (!string.IsNullOrWhiteSpace(userName)) item.Fields["username"] = userName;
        item.Fields["device"] = "Ключ доступа";
        item.Fields["credId"] = B64Url(credId);
        item.Fields["userHandle"] = userHandle ?? "";
        item.Fields["alg"] = "-7";                       // ES256
        item.Fields["privJwk"] = jwk;
        item.Fields["created"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        string id = _vault.Add(item);
        Save();
        if (VaultScreen.IsVisible) { LoadEntries(selectFirst: false); RenderSidebar(); }
        return Resp(new { ok = true, id, credId = B64Url(credId), x, y, spki = B64Url(spki) });
    }

    // Authentication: sign authData||clientDataHash with the stored P-256 private key, in-process.
    // The extension sends only the bytes to sign (b64url) and receives a raw r||s signature — the
    // key stays in the vault process. Works for keys created here and for legacy WebCrypto JWKs.
    private string BridgePasskeySign(string rpId, string credId, string dataB64Url)
    {
        if (_vault is null) return Resp(new { ok = false, error = "locked" });
        if (string.IsNullOrWhiteSpace(rpId) || string.IsNullOrWhiteSpace(credId) || string.IsNullOrWhiteSpace(dataB64Url))
            return Resp(new { ok = false, error = "bad_request" });

        var rec = _vault.Items().FirstOrDefault(z => z.Item.Type == "passkey"
            && string.Equals(z.Item.Fields.GetValueOrDefault("rpId", ""), rpId, StringComparison.OrdinalIgnoreCase)
            && z.Item.Fields.GetValueOrDefault("credId", "") == credId
            && !string.IsNullOrEmpty(z.Item.Fields.GetValueOrDefault("privJwk", "")));
        if (rec is null) return Resp(new { ok = false, error = "no_credential" });

        try
        {
            using var jwk = JsonDocument.Parse(rec.Item.Fields["privJwk"]);
            var root = jwk.RootElement;
            var ecp = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = B64UrlDec(root.GetProperty("d").GetString() ?? ""),
                Q = new ECPoint
                {
                    X = B64UrlDec(root.GetProperty("x").GetString() ?? ""),
                    Y = B64UrlDec(root.GetProperty("y").GetString() ?? ""),
                },
            };
            using var ec = ECDsa.Create(ecp);
            byte[] sig = ec.SignData(B64UrlDec(dataB64Url), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return Resp(new { ok = true, signature = B64Url(sig) });
        }
        catch { return Resp(new { ok = false, error = "sign_failed" }); }
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] B64UrlDec(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    private void BridgeFocus()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        ForceForeground();
        // The FIRST-EVER Show() of a tray-started window maps it asynchronously — by the time
        // the OS window exists, the foreground moment has passed and it lands BEHIND the
        // browser. Repeat the foreground pass shortly after so it reliably comes out on top.
        DispatcherTimer.RunOnce(ForceForeground, TimeSpan.FromMilliseconds(250));
        DispatcherTimer.RunOnce(ForceForeground, TimeSpan.FromMilliseconds(700));
        // Surfaced by the user (via the extension) → offer Hello unlock if the vault is locked.
        if (_vault is null && !Creating && !IsLocked && HasHelloCache())
            _ = TryQuickUnlockHelloAsync();
    }

    private void ForceForeground()
    {
        try
        {
            var h = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (h != IntPtr.Zero)
            {
                ShowWindow(h, 9);   // SW_RESTORE
                // Classic foreground-lock bypass: a synthetic Alt tap makes Windows treat the
                // process as input-active, so SetForegroundWindow is honoured.
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, 2, UIntPtr.Zero);
                uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                uint cur = GetCurrentThreadId();
                if (fg != cur) AttachThreadInput(fg, cur, true);
                SetForegroundWindow(h);
                if (fg != cur) AttachThreadInput(fg, cur, false);
            }
        }
        catch { /* best effort */ }
        Topmost = true; Topmost = false;
        Activate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
