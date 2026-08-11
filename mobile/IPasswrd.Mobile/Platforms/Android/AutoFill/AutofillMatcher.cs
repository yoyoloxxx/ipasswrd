using IPasswrd.Core;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Platforms.Android.AutoFill;

/// <summary>Одна строка списка автозаполнения.</summary>
internal sealed record AutofillCandidate(string Id, VaultItem Item, int Score)
{
    public string Title => Item.Title.Length > 0 ? Item.Title : "Без названия";
    public string Login => Item.Fields.GetValueOrDefault("username", "");
    public string Password => Item.Fields.GetValueOrDefault("password", "");
    public string Totp => Item.Fields.GetValueOrDefault("totp", "");
}

/// <summary>
/// Подбор записей сейфа под экран, который заполняем. В браузере совпадение точное
/// (регистрируемый домен), в нативном приложении — по имени пакета: com.vk.vkapp ↔ vk.com.
/// </summary>
internal static class AutofillMatcher
{
    /// <summary>Все аккаунты, отсортированные: сначала подходящие под домен/пакет.</summary>
    public static List<AutofillCandidate> Rank(Vault vault, string? webDomain, string? packageName)
    {
        string wantDomain = Dedup.RegistrableDomain(Normalize(webDomain));
        string[] pkgParts = PackageParts(packageName);

        var list = new List<AutofillCandidate>();
        foreach (VaultEntry e in vault.Items())
        {
            if (e.Item.Type != "account") continue;

            string url = e.Item.Fields.GetValueOrDefault("url", "");
            string dom = Dedup.RegistrableDomain(url);
            int score = 0;

            if (wantDomain.Length > 0 && dom.Length > 0)
            {
                if (string.Equals(dom, wantDomain, StringComparison.OrdinalIgnoreCase)) score = 100;
                else if (dom.EndsWith("." + wantDomain, StringComparison.OrdinalIgnoreCase)
                      || wantDomain.EndsWith("." + dom, StringComparison.OrdinalIgnoreCase)) score = 80;
            }

            if (score == 0 && pkgParts.Length > 0)
            {
                string brand = BrandOf(dom);
                if (brand.Length >= 3 && pkgParts.Contains(brand, StringComparer.OrdinalIgnoreCase)) score = 70;
                else
                {
                    string titleBrand = BrandOf(e.Item.Title.ToLowerInvariant());
                    if (titleBrand.Length >= 3 && pkgParts.Contains(titleBrand, StringComparer.OrdinalIgnoreCase))
                        score = 60;
                }
            }

            list.Add(new AutofillCandidate(e.Id, e.Item, score));
        }

        return list
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Только уверенные совпадения — их показываем сразу над клавиатурой.</summary>
    public static List<AutofillCandidate> Matches(Vault vault, string? webDomain, string? packageName)
        => Rank(vault, webDomain, packageName).Where(c => c.Score > 0).ToList();

    /// <summary>Код проверки для записи: свой totp либо привязанный из «Кодов».</summary>
    public static string? CodeFor(Vault vault, VaultItem account)
    {
        try
        {
            string own = account.Fields.GetValueOrDefault("totp", "").Trim();
            if (own.Length > 0) return IPasswrd.Core.Totp.GenerateFrom(own, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            string key = SiteGroups.KeyFor(account);
            foreach (VaultEntry e in vault.Items())
            {
                if (e.Item.Type != "totp") continue;
                string secret = e.Item.Fields.GetValueOrDefault("totp", "").Trim();
                if (secret.Length == 0) continue;
                if (TotpMeta.MatchesSite(e.Item, key))
                    return IPasswrd.Core.Totp.GenerateFrom(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
        }
        catch (Exception) { }
        return null;
    }

    private static string Normalize(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return "";
        string d = domain.Trim();
        return d.Contains("://", StringComparison.Ordinal) ? d : "https://" + d;
    }

    private static string[] PackageParts(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return Array.Empty<string>();
        // com.vk.vkapp → [com, vk, vkapp]; служебные части браузеров отбрасываем
        return packageName.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p is not ("com" or "org" or "net" or "ru" or "app" or "android" or "mobile"))
            .ToArray();
    }

    private static string BrandOf(string domainOrTitle)
    {
        if (string.IsNullOrEmpty(domainOrTitle)) return "";
        int dot = domainOrTitle.IndexOf('.');
        string head = dot > 0 ? domainOrTitle[..dot] : domainOrTitle;
        return new string(head.Where(char.IsLetterOrDigit).ToArray());
    }
}
