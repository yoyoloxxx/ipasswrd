using System.Text;
using System.Text.RegularExpressions;
using Foundation;
using Security;

namespace IPasswrd.AutoFill;

/// <summary>Доступ расширения к общим данным приложения.
/// AltStore при подписи переименовывает App Group (добавляет суффикс команды),
/// поэтому реальное имя группы читаем из своего embedded.mobileprovision,
/// а зашитое имя — только запасной вариант.</summary>
internal static class AppGroup
{
    private const string FallbackGroup = "group.com.yoyoloxxx.ipasswrd";
    /// <summary>v2: DEK под SecAccessControl BiometryCurrentSet + shared keychain-access-group.
    /// Старый v1 (без биометрии) при чтении игнорируем и стираем.</summary>
    private const string DekService = "ipw.autofill.dek";
    private const string DekAccount = "v2";
    private const string DekAccountLegacy = "v1";
    private const string LockoutFailsKey = "lockout.fails";
    private const string LockoutUntilKey = "lockout.until";

    private static string? _group;
    private static string? _keychainGroup;

    internal static string GroupId()
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
        catch { /* ниже запасной вариант */ }
        return _group = FallbackGroup;
    }

    /// <summary>Общая keychain-access-group хоста и расширения (TeamID.com.yoyoloxxx.ipasswrd).
    /// Нужна, чтобы основное приложение могло стереть DEK при Lock().</summary>
    internal static string? KeychainAccessGroup()
    {
        if (_keychainGroup is not null) return _keychainGroup.Length == 0 ? null : _keychainGroup;
        try
        {
            string? p = NSBundle.MainBundle.PathForResource("embedded", "mobileprovision");
            if (p is not null)
            {
                string s = Encoding.ASCII.GetString(File.ReadAllBytes(p));
                Match block = Regex.Match(s,
                    "<key>keychain-access-groups</key>\\s*<array>(.*?)</array>",
                    RegexOptions.Singleline);
                if (block.Success)
                {
                    foreach (Match m in Regex.Matches(block.Groups[1].Value, "<string>([^<]+)</string>"))
                    {
                        string g = m.Groups[1].Value.Trim();
                        // Хостовая группа, не *.AutoFill
                        if (g.EndsWith("com.yoyoloxxx.ipasswrd", StringComparison.Ordinal)
                            && !g.Contains("AutoFill", StringComparison.Ordinal))
                            return _keychainGroup = g;
                    }
                }
            }
        }
        catch { }
        _keychainGroup = "";
        return null;
    }

    /// <summary>Путь к копии сейфа в общем контейнере (пишет основное приложение).</summary>
    internal static string? VaultPath()
    {
        try
        {
            NSUrl? url = NSFileManager.DefaultManager.GetContainerUrl(GroupId());
            string? root = url?.Path;
            return root is null ? null : Path.Combine(root, "vault.ipvault");
        }
        catch { return null; }
    }

    internal static byte[]? ReadVault()
    {
        try
        {
            string? p = VaultPath();
            return p is not null && File.Exists(p) ? File.ReadAllBytes(p) : null;
        }
        catch { return null; }
    }

    // ===== кэш сессионного ключа (быстрая разблокировка расширения, 30 дней) =====

    private sealed class DekData
    {
        public string Dek { get; set; } = "";
        public long ExpiresAt { get; set; }
    }

    private static SecRecord DekQuery(string account)
    {
        var q = new SecRecord(SecKind.GenericPassword)
        {
            Service = DekService,
            Account = account,
        };
        string? ag = KeychainAccessGroup();
        if (ag is not null) q.AccessGroup = ag;
        return q;
    }

    /// <summary>Читает DEK; OS покажет Face ID / Touch ID (BiometryCurrentSet).
    /// Вызывать с фонового потока — QueryAsData блокирует до жеста.</summary>
    internal static byte[]? LoadDek()
    {
        try
        {
            // Старый незащищённый кэш — стереть, не использовать.
            try { SecKeyChain.Remove(DekQuery(DekAccountLegacy)); } catch { }

            var q = DekQuery(DekAccount);
            q.UseOperationPrompt = "Открыть сейф для автозаполнения";
            NSData? data = SecKeyChain.QueryAsData(q, false, out SecStatusCode st);
            if (st != SecStatusCode.Success || data is null) return null;
            var d = System.Text.Json.JsonSerializer.Deserialize<DekData>(data.ToArray());
            if (d is null || string.IsNullOrEmpty(d.Dek)) return null;
            if (d.ExpiresAt != 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > d.ExpiresAt) { WipeDek(); return null; }
            return Convert.FromBase64String(d.Dek);
        }
        catch { return null; }
    }

    internal static void SaveDek(byte[] dek)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new DekData
            {
                Dek = Convert.ToBase64String(dek),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(),
            });
            try { SecKeyChain.Remove(DekQuery(DekAccountLegacy)); } catch { }
            SecKeyChain.Remove(DekQuery(DekAccount));

            // BiometryCurrentSet: DEK привязан к текущему набору биометрии; без passcode-фолбэка
            // (запасной вход — мастер-пароль в UI расширения). Паритет с BiometricKeychainIos.
            using var access = new SecAccessControl(
                SecAccessible.WhenUnlockedThisDeviceOnly, SecAccessControlCreateFlags.BiometryCurrentSet);
            var q = DekQuery(DekAccount);
            q.ValueData = NSData.FromArray(payload);
            q.AccessControl = access;
            SecKeyChain.Add(q);
        }
        catch { /* необязательный путь */ }
    }

    /// <summary>Стереть кэш DEK (вызывает и расширение, и хост при Lock через AutoFillDek).</summary>
    internal static void WipeDek()
    {
        try { SecKeyChain.Remove(DekQuery(DekAccount)); } catch { }
        try { SecKeyChain.Remove(DekQuery(DekAccountLegacy)); } catch { }
        // На случай записи без AccessGroup (до шаринга) — подчистить и дефолтную группу.
        try
        {
            SecKeyChain.Remove(new SecRecord(SecKind.GenericPassword) { Service = DekService, Account = DekAccount });
            SecKeyChain.Remove(new SecRecord(SecKind.GenericPassword) { Service = DekService, Account = DekAccountLegacy });
        }
        catch { }
    }

    // ===== общий локаут с основным приложением (App Group UserDefaults) =====

    private static NSUserDefaults? SharedDefaults()
    {
        try { return NSUserDefaults.FromSuiteName(GroupId()); }
        catch { return null; }
    }

    internal static int LockoutFails
    {
        get
        {
            try
            {
                string? s = SharedDefaults()?.StringForKey(LockoutFailsKey);
                return int.TryParse(s, out int n) ? n : 0;
            }
            catch { return 0; }
        }
        set
        {
            try
            {
                NSUserDefaults? d = SharedDefaults();
                if (d is null) return;
                d.SetString(value.ToString(), LockoutFailsKey);
                d.Synchronize();
            }
            catch { }
        }
    }

    internal static DateTimeOffset LockoutUntil
    {
        get
        {
            try
            {
                string? s = SharedDefaults()?.StringForKey(LockoutUntilKey);
                long sec = long.TryParse(s, out long n) ? n : 0L;
                return DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, sec));
            }
            catch { return DateTimeOffset.UnixEpoch; }
        }
        set
        {
            try
            {
                NSUserDefaults? d = SharedDefaults();
                if (d is null) return;
                long sec = Math.Max(0, value.ToUnixTimeSeconds());
                d.SetString(sec.ToString(), LockoutUntilKey);
                d.Synchronize();
            }
            catch { }
        }
    }

    internal static TimeSpan LockoutRemaining
    {
        get
        {
            TimeSpan left = LockoutUntil - DateTimeOffset.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }
}

/// <summary>Публичная точка для хоста: стереть AutoFill DEK при блокировке сейфа.</summary>
public static class AutoFillDek
{
    public static void Wipe() => AppGroup.WipeDek();
}
