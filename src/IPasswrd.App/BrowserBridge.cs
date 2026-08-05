using Avalonia.Controls;
using Avalonia.Threading;
using System.IO.Pipes;
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

    private async Task<string> HandleBridgeRequest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string cmd = root.TryGetProperty("cmd", out var c) ? (c.GetString() ?? "") : "";
            string Get(string name) => root.TryGetProperty(name, out var v) ? (v.GetString() ?? "") : "";

            switch (cmd)
            {
                case "status":
                    return await OnUi(() => Resp(new { ok = true, unlocked = _vault is not null }));
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
                case "passkeySave":
                {
                    string rpId = Get("rpId"), credId = Get("credId"), userHandle = Get("userHandle"),
                           userName = Get("userName"), privJwk = Get("privJwk");
                    return await OnUi(() => BridgePasskeySave(rpId, credId, userHandle, userName, privJwk));
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

    // Cards and documents for the in-field picker (not site-specific). Only while unlocked.
    private string BridgeList()
    {
        if (_vault is null) return Resp(new { ok = true, unlocked = false, cards = Array.Empty<object>(), docs = Array.Empty<object>() });
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
        return Resp(new { ok = true, unlocked = true, cards, docs });
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
        SaveQuickUnlock();
        EnterVault();
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
                privJwk = x.Item.Fields.GetValueOrDefault("privJwk", ""),
            })
            .ToList();
        return Resp(new { ok = true, unlocked = true, items });
    }

    private string BridgePasskeySave(string rpId, string credId, string userHandle, string userName, string privJwk)
    {
        if (_vault is null) return Resp(new { ok = false, error = "locked" });
        if (string.IsNullOrWhiteSpace(rpId) || string.IsNullOrWhiteSpace(credId) || string.IsNullOrWhiteSpace(privJwk))
            return Resp(new { ok = false, error = "bad_request" });

        var item = new VaultItem
        {
            Type = "passkey",
            Title = rpId,                                 // name after the site only; the login lives in "username" (shown as the subtitle)
        };
        item.Fields["rpId"] = rpId;
        item.Fields["url"] = rpId;                       // detail view shows this as "Сайт"
        if (!string.IsNullOrWhiteSpace(userName)) item.Fields["username"] = userName;
        item.Fields["device"] = "Ключ доступа";
        item.Fields["credId"] = credId;
        item.Fields["userHandle"] = userHandle ?? "";
        item.Fields["alg"] = "-7";                       // ES256
        item.Fields["privJwk"] = privJwk;
        item.Fields["created"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

        string id = _vault.Add(item);
        Save();
        if (VaultScreen.IsVisible) { LoadEntries(selectFirst: false); RenderSidebar(); }
        return Resp(new { ok = true, id });
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
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
