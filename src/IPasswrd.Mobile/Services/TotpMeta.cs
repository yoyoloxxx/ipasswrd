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

    // ================= автопривязка «как на ПК» =================

    /// <summary>«https://google.com/» и «Google» → «google» — тот же BrandToken, что на ПК.</summary>
    public static string BrandToken(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim().ToLowerInvariant();
        int p = s.IndexOf("://", StringComparison.Ordinal); if (p >= 0) s = s[(p + 3)..];
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
        int slash = s.IndexOf('/'); if (slash >= 0) s = s[..slash];
        if (s.Contains('.'))
        {
            string rd = Dedup.RegistrableDomain("https://" + s);
            if (!string.IsNullOrEmpty(rd)) s = rd;
            var parts = s.Split('.');
            return parts.Length >= 2 ? parts[^2] : s;
        }
        return new string(s.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>Сколько аккаунтов делят этот регистрируемый домен (для кодов без логина).</summary>
    public static int SiteAccountCount(Vault v, string site)
    {
        if (string.IsNullOrEmpty(site)) return 0;
        try
        {
            return v.Items().Count(x => x.Item.Type == "account"
                && Dedup.RegistrableDomain(x.Item.Fields.GetValueOrDefault("url", "")) == site);
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Код из «Кодов», ОДНОЗНАЧНО принадлежащий этому аккаунту — логика ПК один в один:
    /// совпал бренд сайта и логин (в поле username записи кода или в otpauth://), либо код без логина,
    /// когда у сайта единственный аккаунт. Возвращает секрет или null (нет / неоднозначно).</summary>
    public static string? FindLinkedTotp(Vault v, VaultItem account)
    {
        string brand = BrandToken(account.Fields.GetValueOrDefault("url", ""));
        if (brand.Length == 0) return null;
        string site = Dedup.RegistrableDomain(account.Fields.GetValueOrDefault("url", ""));
        string user = account.Fields.GetValueOrDefault("username", "").Trim().ToLowerInvariant();
        string? strong = null; int strongCount = 0;   // тот же сайт И тот же логин
        string? weak = null; int weakCount = 0;       // тот же сайт, логин в коде не указан
        try
        {
            foreach (VaultEntry e in v.Items())
            {
                if (e.Item.Type != "totp") continue;
                string sec = e.Item.Fields.GetValueOrDefault("totp", "");
                if (string.IsNullOrWhiteSpace(sec)) continue;
                string issuer = "", acct = "";
                try { TotpConfig cfg = Totp.Parse(sec); issuer = cfg.Issuer ?? ""; acct = cfg.Account ?? ""; }
                catch { /* голый секрет */ }
                string tbrand = BrandToken(issuer);
                if (tbrand.Length == 0) tbrand = BrandToken(e.Item.Title);
                if (tbrand.Length == 0 || tbrand != brand) continue;
                string tuser = e.Item.Fields.GetValueOrDefault("username", "").Trim().ToLowerInvariant();
                if (tuser.Length == 0) tuser = acct.Trim().ToLowerInvariant();
                if (user.Length > 0 && tuser.Length > 0 && tuser == user) { strong = sec; strongCount++; }
                else if (tuser.Length == 0) { weak = sec; weakCount++; }
                // тот же сайт, но другой логин → код другого аккаунта, пропускаем
            }
        }
        catch (Exception) { return null; }
        if (strongCount == 1) return strong;
        if (strongCount == 0 && weakCount == 1 && SiteAccountCount(v, site) <= 1) return weak;
        return null;
    }
}
