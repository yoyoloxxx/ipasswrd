using IPasswrd.Core;

namespace IPasswrd.Mobile.Services;

/// <summary>Сопоставление отдельных totp-записей («Коды») с аккаунтами по сайту:
/// «google.com» ↔ запись «google» — по названию записи, issuer или account из otpauth://.
/// Сравнение нестрогое, но с защитой от коротких совпадений (mail ⊄ gmail).</summary>
public static class TotpMeta
{
    /// <summary>Issuer и account из значения totp-поля (пусто, если это голый Base32-секрет).</summary>
    public static (string Issuer, string Account) IssuerAccount(string secretOrUri)
    {
        try
        {
            TotpConfig cfg = Totp.Parse(secretOrUri ?? "");
            return (cfg.Issuer ?? "", cfg.Account ?? "");
        }
        catch (Exception) { return ("", ""); }
    }

    /// <summary>База сайта для сравнения: «google.com» → «google»; IP остаётся целиком.</summary>
    public static string SiteBase(string key)
    {
        string k = (key ?? "").Trim().ToLowerInvariant();
        if (k.Length == 0) return "";
        bool ipLike = k.All(c => char.IsDigit(c) || c == '.' || c == ':');
        if (ipLike) return Norm(k);
        int dot = k.IndexOf('.');
        return Norm(dot > 0 ? k[..dot] : k);
    }

    /// <summary>Подходит ли отдельная totp-запись к сайту аккаунта (siteKey = SiteGroups.KeyFor).</summary>
    public static bool MatchesSite(VaultItem totpRecord, string siteKey)
    {
        string baseName = SiteBase(siteKey);
        if (baseName.Length == 0) return false;

        var (issuer, account) = IssuerAccount(totpRecord.Fields.GetValueOrDefault("totp", ""));
        return Like(totpRecord.Title, baseName) || Like(issuer, baseName) || Like(account, baseName);
    }

    private static bool Like(string candidate, string baseName)
    {
        string c = Norm(candidate);
        if (c.Length == 0) return false;
        if (c == baseName) return true;
        // подстрочные совпадения — только для достаточно длинных имён,
        // иначе «mail» ловил бы «gmail» и т.п.
        return c.Length >= 5 && baseName.Length >= 5 && (c.Contains(baseName) || baseName.Contains(c));
    }

    private static string Norm(string s) =>
        new((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
