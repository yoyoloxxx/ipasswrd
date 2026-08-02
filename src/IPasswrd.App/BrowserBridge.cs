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
    private string BridgeCredentials(string url)
    {
        if (_vault is null) return Resp(new { ok = true, unlocked = false, items = Array.Empty<object>() });
        string baseDom = Dedup.RegistrableDomain(url);
        if (string.IsNullOrEmpty(baseDom)) return Resp(new { ok = true, unlocked = true, items = Array.Empty<object>() });
        string nu = NormUrl(url);

        var items = _vault.Items()
            .Where(x => x.Item.Type == "account" &&
                        Dedup.RegistrableDomain(x.Item.Fields.GetValueOrDefault("url", "")) == baseDom)
            .OrderBy(x => NormUrl(x.Item.Fields.GetValueOrDefault("url", "")) == nu ? 0 : 1)
            .ThenBy(x => Dedup.HostDepth(x.Item.Fields.GetValueOrDefault("url", "")))
            .ThenBy(x => x.Item.Fields.GetValueOrDefault("username", ""), StringComparer.OrdinalIgnoreCase)
            .Select(x => new
            {
                id = x.Id,
                title = HeaderName(x.Item),
                username = x.Item.Fields.GetValueOrDefault("username", ""),
                password = x.Item.Fields.GetValueOrDefault("password", ""),
                url = x.Item.Fields.GetValueOrDefault("url", ""),
                totp = TotpNow(x.Item.Fields.GetValueOrDefault("totp", "")),
            })
            .ToList();
        return Resp(new { ok = true, unlocked = true, items });
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
