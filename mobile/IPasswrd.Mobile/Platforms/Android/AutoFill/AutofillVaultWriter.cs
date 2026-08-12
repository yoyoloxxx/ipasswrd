using IPasswrd.Core;
using IPasswrd.Mobile.Services;

namespace IPasswrd.Mobile.Platforms.Android.AutoFill;

/// <summary>
/// Запись логина/пароля, пойманного системным «Сохранить пароль?», в сейф.
/// Если такой аккаунт уже есть — обновляем пароль (Vault.Update сам ведёт историю),
/// иначе создаём новую запись.
/// </summary>
internal static class AutofillVaultWriter
{
    public static async Task<bool> SaveAsync(string username, string password, string domain, string packageName)
    {
        Vault? vault = Svc.State.Vault;
        if (vault is null || password.Length == 0) return false;

        string url = BuildUrl(domain);
        string reg = Dedup.RegistrableDomain(url);

        VaultEntry? existing = null;
        foreach (VaultEntry e in vault.Items())
        {
            if (e.Item.Type != "account") continue;
            string itemDomain = Dedup.RegistrableDomain(e.Item.Fields.GetValueOrDefault("url", ""));
            string itemUser = e.Item.Fields.GetValueOrDefault("username", "");
            string itemPkg = e.Item.Fields.GetValueOrDefault("androidPackage", "");
            bool sameSite = reg.Length > 0
                ? string.Equals(itemDomain, reg, StringComparison.OrdinalIgnoreCase)
                : packageName.Length > 0 && string.Equals(itemPkg, packageName, StringComparison.OrdinalIgnoreCase);
            bool sameUser = username.Length == 0 || string.Equals(itemUser, username, StringComparison.OrdinalIgnoreCase);
            if (sameSite && sameUser) { existing = e; break; }
        }

        if (existing is not null)
        {
            if (existing.Item.Fields.GetValueOrDefault("password", "") == password) return false;   // нечего менять
            VaultItem updated = existing.Item;
            updated.Fields["password"] = password;
            if (username.Length > 0) updated.Fields["username"] = username;
            vault.Update(existing.Id, updated);
        }
        else
        {
            var item = new VaultItem
            {
                Type = "account",
                Title = TitleFor(reg, packageName),
            };
            if (url.Length > 0) item.Fields["url"] = url;
            else if (packageName.Length > 0) item.Fields["androidPackage"] = packageName;   // native-app association, for exact-match autofill
            if (username.Length > 0) item.Fields["username"] = username;
            item.Fields["password"] = password;
            vault.Add(item);
        }

        await Svc.State.SaveAsync();
        return true;
    }

    private static string BuildUrl(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return "";
        string d = domain.Trim();
        return d.Contains("://", StringComparison.Ordinal) ? d : "https://" + d;
    }

    private static string TitleFor(string registrableDomain, string packageName)
    {
        string src = registrableDomain;
        if (src.Length == 0 && packageName.Length > 0)
        {
            string[] parts = packageName.Split('.', StringSplitOptions.RemoveEmptyEntries);
            src = parts.Length > 1 ? parts[1] : packageName;
        }
        if (src.Length == 0) return "Новая запись";

        int dot = src.IndexOf('.');
        string head = dot > 0 ? src[..dot] : src;
        return head.Length > 0 ? char.ToUpperInvariant(head[0]) + head[1..] : src;
    }
}
