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
    private const string DekService = "ipw.autofill.dek";

    internal static string GroupId()
    {
        try
        {
            string? p = NSBundle.MainBundle.PathForResource("embedded", "mobileprovision");
            if (p is not null)
            {
                string s = Encoding.ASCII.GetString(File.ReadAllBytes(p));
                Match m = Regex.Match(s,
                    "<key>com\\.apple\\.security\\.application-groups</key>\\s*<array>\\s*<string>([^<]+)</string>",
                    RegexOptions.Singleline);
                if (m.Success) return m.Groups[1].Value.Trim();
            }
        }
        catch { /* ниже запасной вариант */ }
        return FallbackGroup;
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

    internal static byte[]? LoadDek()
    {
        try
        {
            var q = new SecRecord(SecKind.GenericPassword) { Service = DekService, Account = "v1" };
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
            var q = new SecRecord(SecKind.GenericPassword) { Service = DekService, Account = "v1" };
            SecKeyChain.Remove(q);
            q.ValueData = NSData.FromArray(payload);
            q.Accessible = SecAccessible.WhenUnlockedThisDeviceOnly;
            SecKeyChain.Add(q);
        }
        catch { /* необязательный путь */ }
    }

    internal static void WipeDek()
    {
        try { SecKeyChain.Remove(new SecRecord(SecKind.GenericPassword) { Service = DekService, Account = "v1" }); }
        catch { }
    }
}
