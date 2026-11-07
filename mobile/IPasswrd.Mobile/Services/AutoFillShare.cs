using System.Text;
using System.Text.RegularExpressions;
using IPasswrd.Core;

#if IOS
using AuthenticationServices;
using Foundation;
#endif

namespace IPasswrd.Mobile.Services;

/// <summary>Мост к системному автозаполнению iOS: копия сейфа в общем контейнере
/// (App Group) для расширения + подсказки логинов над клавиатурой
/// (ASCredentialIdentityStore). AltStore при подписи переименовывает группу,
/// поэтому её настоящее имя читаем из embedded.mobileprovision.</summary>
public static class AutoFillShare
{
    public const string FallbackGroup = "group.com.yoyoloxxx.ipasswrd";
    private const string LockoutFailsKey = "lockout.fails";
    private const string LockoutUntilKey = "lockout.until";
    /// <summary>Anti-downgrade marker (audit M12): vaultId known to be MAC-sealed on this install.</summary>
    private const string AuthVaultIdKey = "vault.authenticatedId";

#if IOS
    private static string? _group;

    private static string GroupId()
    {
        if (_group is not null) return _group;
        try
        {
            string? p = NSBundle.MainBundle.PathForResource("embedded", "mobileprovision");
            if (p is not null)
            {
                string s = Encoding.ASCII.GetString(File.ReadAllBytes(p));
                Match m = Regex.Match(s,
                    "<key>com\\.apple\\.security\\.application-groups</key>\\s*<array>\\s*<string>([^<]+)</string>",
                    RegexOptions.Singleline);
                if (m.Success) return _group = m.Groups[1].Value.Trim();
            }
        }
        catch { }
        return _group = FallbackGroup;
    }

    private static NSUserDefaults? SharedDefaults()
    {
        try { return NSUserDefaults.FromSuiteName(GroupId()); }
        catch { return null; }
    }

    private static string? GroupVaultPath()
    {
        try
        {
            NSUrl? url = NSFileManager.DefaultManager.GetContainerUrl(GroupId());
            string? root = url?.Path;
            return root is null ? null : Path.Combine(root, "vault.ipvault");
        }
        catch { return null; }
    }

    /// <summary>Положить свежую (зашифрованную) копию сейфа в общий контейнер.</summary>
    public static void MirrorVault(byte[] data)
    {
        try
        {
            string? p = GroupVaultPath();
            if (p is null) return;
            File.WriteAllBytes(p, data);
        }
        catch { /* автозаполнение — необязательный путь */ }
    }

    /// <summary>Стереть кэш DEK расширения при блокировке хоста (shared keychain-access-group).</summary>
    public static void WipeDek()
    {
        try { IPasswrd.AutoFill.AutoFillDek.Wipe(); }
        catch { /* расширение / keychain могут быть недоступны */ }
    }

    /// <summary>Общий счётчик неудачных попыток мастер-пароля (App Group ↔ AutoFill).</summary>
    public static int GetLockoutFails()
    {
        try
        {
            NSUserDefaults? d = SharedDefaults();
            string? s = d?.StringForKey(LockoutFailsKey);
            if (!string.IsNullOrEmpty(s) && int.TryParse(s, out int n))
                return n;
        }
        catch { }
        // Миграция со старых приватных Preferences хоста.
        int legacy = Preferences.Get(LockoutFailsKey, 0);
        if (legacy != 0) SetLockoutFails(legacy);
        return legacy;
    }

    public static void SetLockoutFails(int value)
    {
        try
        {
            NSUserDefaults? d = SharedDefaults();
            if (d is not null)
            {
                d.SetString(value.ToString(), LockoutFailsKey);
                d.Synchronize();
            }
        }
        catch { }
        try { Preferences.Set(LockoutFailsKey, value); } catch { }
    }

    public static long GetLockoutUntilUnix()
    {
        try
        {
            NSUserDefaults? d = SharedDefaults();
            string? s = d?.StringForKey(LockoutUntilKey);
            if (!string.IsNullOrEmpty(s) && long.TryParse(s, out long n))
                return Math.Max(0, n);
        }
        catch { }
        long legacy = Preferences.Get(LockoutUntilKey, 0L);
        if (legacy != 0) SetLockoutUntilUnix(legacy);
        return legacy;
    }

    public static void SetLockoutUntilUnix(long unixSeconds)
    {
        if (unixSeconds < 0) unixSeconds = 0;
        try
        {
            NSUserDefaults? d = SharedDefaults();
            if (d is not null)
            {
                d.SetString(unixSeconds.ToString(), LockoutUntilKey);
                d.Synchronize();
            }
        }
        catch { }
        try { Preferences.Set(LockoutUntilKey, unixSeconds); } catch { }
    }

    /// <summary>Persist the MAC-authenticated vaultId into the App Group so AutoFill
    /// can pass <c>requireAuthenticated: true</c> (audit M12).</summary>
    public static void SetAuthenticatedVaultId(string vaultId)
    {
        if (string.IsNullOrEmpty(vaultId)) return;
        try
        {
            NSUserDefaults? d = SharedDefaults();
            if (d is not null)
            {
                d.SetString(vaultId, AuthVaultIdKey);
                d.Synchronize();
            }
        }
        catch { }
        try { Preferences.Set(AuthVaultIdKey, vaultId); } catch { }
    }

    public static string GetAuthenticatedVaultId()
    {
        try
        {
            NSUserDefaults? d = SharedDefaults();
            string? s = d?.StringForKey(AuthVaultIdKey);
            if (!string.IsNullOrEmpty(s)) return s;
        }
        catch { }
        return Preferences.Get(AuthVaultIdKey, "");
    }

    /// <summary>Обновить подсказки над клавиатурой: логины (домен + имя) и,
    /// на iOS 18+, одноразовые коды проверки для полей кода.</summary>
    public static void UpdateIdentities(Vault v)
    {
        try
        {
            // Подсказкам над клавиатурой нужны домен и логин. Список целиком — это ещё и все
            // вложения разом, а они здесь ни при чём; отпускаем их сразу после расшифровки.
            var entries = new List<VaultEntry>();
            foreach (VaultEntry e in v.Stream())
            {
                e.Item.Attachments.Clear();
                e.Item.History.Clear();
                entries.Add(e);
            }
            var pwd = new List<ASPasswordCredentialIdentity>();
            var all = new List<IASCredentialIdentity>();
            bool otcSupported = OperatingSystem.IsIOSVersionAtLeast(18);

            foreach (VaultEntry e in entries)
            {
                if (e.Item.Type != "account") continue;
                string user = e.Item.Fields.GetValueOrDefault("username", "");
                string dom = Dedup.RegistrableDomain(e.Item.Fields.GetValueOrDefault("url", ""));
                if (user.Length == 0 || dom.Length == 0) continue;

                var svc = new ASCredentialServiceIdentifier(dom, ASCredentialServiceIdentifierType.Domain);
                var pid = new ASPasswordCredentialIdentity(svc, user, e.Id);
                pwd.Add(pid);
                all.Add(pid);

                // Код проверки прямо в записи аккаунта → подсказка кода для этого домена.
                if (otcSupported && e.Item.Fields.GetValueOrDefault("totp", "").Trim().Length > 0)
                    all.Add(new ASOneTimeCodeCredentialIdentity(svc, user, e.Id));
            }

            if (otcSupported)
            {
                // Отдельные записи из «Кодов»: домены берём у подходящих аккаунтов (google ↔ google.com).
                var accounts = entries.Where(x => x.Item.Type == "account").ToList();
                foreach (VaultEntry t in entries)
                {
                    if (t.Item.Type != "totp") continue;
                    if (t.Item.Fields.GetValueOrDefault("totp", "").Trim().Length == 0) continue;
                    var doms = accounts
                        .Where(a => TotpMeta.MatchesSite(t.Item, SiteGroups.KeyFor(a.Item)))
                        .Select(a => Dedup.RegistrableDomain(a.Item.Fields.GetValueOrDefault("url", "")))
                        .Where(d => d.Length > 0)
                        .Distinct(StringComparer.Ordinal);
                    string label = t.Item.Title.Length > 0 ? t.Item.Title : "код";
                    foreach (string dom in doms)
                        all.Add(new ASOneTimeCodeCredentialIdentity(
                            new ASCredentialServiceIdentifier(dom, ASCredentialServiceIdentifierType.Domain),
                            label, t.Id));
                }
            }

            ASCredentialIdentityStore.SharedStore.GetCredentialIdentityStoreState(state =>
            {
                if (state is null || !state.Enabled) return;
                if (OperatingSystem.IsIOSVersionAtLeast(17))
                    ASCredentialIdentityStore.SharedStore.ReplaceCredentialIdentityEntries(all.ToArray(), (ok, err) => { });
                else
                    ASCredentialIdentityStore.SharedStore.ReplaceCredentialIdentities(pwd.ToArray(), (ok, err) => { });
            });
        }
        catch { /* стор может быть недоступен, это не критично */ }
    }
#else
    public static void MirrorVault(byte[] data) { }
    public static void UpdateIdentities(Vault v) { }
    public static void WipeDek() { }

    public static int GetLockoutFails() => Preferences.Get(LockoutFailsKey, 0);
    public static void SetLockoutFails(int value) => Preferences.Set(LockoutFailsKey, value);
    public static long GetLockoutUntilUnix() => Preferences.Get(LockoutUntilKey, 0L);
    public static void SetLockoutUntilUnix(long unixSeconds) => Preferences.Set(LockoutUntilKey, unixSeconds);

    public static void SetAuthenticatedVaultId(string vaultId)
    {
        if (!string.IsNullOrEmpty(vaultId)) Preferences.Set(AuthVaultIdKey, vaultId);
    }

    public static string GetAuthenticatedVaultId() => Preferences.Get(AuthVaultIdKey, "");
#endif
}
