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
        string wantPkg = (packageName ?? "").Trim();

        var list = new List<AutofillCandidate>();
        foreach (VaultEntry e in vault.Items())
        {
            if (e.Item.Type != "account") continue;

            string url = e.Item.Fields.GetValueOrDefault("url", "");
            string dom = Dedup.RegistrableDomain(url);
            string itemPkg = e.Item.Fields.GetValueOrDefault("androidPackage", "").Trim();
            int score = 0;

            // Browser target: strict registrable-domain equality on the full Public Suffix List. No
            // subdomain or substring fallback — a saved login is offered to a page ONLY when its eTLD+1
            // matches exactly, so a sibling tenant or a look-alike domain never collects it.
            if (wantDomain.Length > 0 && dom.Length > 0
                && string.Equals(dom, wantDomain, StringComparison.OrdinalIgnoreCase))
                score = 100;

            // Native-app target: EXACT stored package association only (recorded when the login was
            // saved from that very app). No brand/name guessing, so an app can no longer pose as another
            // (e.g. com.paypal.evil matching paypal.com) to be handed its credential.
            else if (wantPkg.Length > 0 && itemPkg.Length > 0
                && string.Equals(itemPkg, wantPkg, StringComparison.OrdinalIgnoreCase))
                score = 90;

            // Курируемый список официальных приложений: ТОЧНЫЙ package id → его регистрируемый
            // домен. Идентичность пакета Android гарантирует подписью при установке, поэтому в
            // отличие от угадывания по имени подделать это нельзя. Явная привязка (запись,
            // сохранённая из самого приложения) всё равно ранжируется выше.
            else if (wantPkg.Length > 0 && dom.Length > 0
                && KnownApps.TryGetValue(wantPkg, out string[]? kdoms)
                && Array.Exists(kdoms, d2 => string.Equals(d2, dom, StringComparison.OrdinalIgnoreCase)))
                score = 85;

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

    /// <summary>Официальные приложения популярных сервисов: точный package id → регистрируемые
    /// домены. Даёт автозаполнение в TikTok/Instagram и т.п. без ручной привязки, не открывая
    /// обратно дыру нечёткого сопоставления имён.</summary>
    private static readonly Dictionary<string, string[]> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["com.zhiliaoapp.musically"] = new[] { "tiktok.com" },
        ["com.ss.android.ugc.trill"] = new[] { "tiktok.com" },
        ["com.instagram.android"] = new[] { "instagram.com" },
        ["com.facebook.katana"] = new[] { "facebook.com" },
        ["com.facebook.lite"] = new[] { "facebook.com" },
        ["com.vkontakte.android"] = new[] { "vk.com" },
        ["com.twitter.android"] = new[] { "x.com", "twitter.com" },
        ["com.netflix.mediaclient"] = new[] { "netflix.com" },
        ["com.spotify.music"] = new[] { "spotify.com" },
        ["com.discord"] = new[] { "discord.com" },
        ["com.reddit.frontpage"] = new[] { "reddit.com" },
        ["com.pinterest"] = new[] { "pinterest.com" },
        ["com.snapchat.android"] = new[] { "snapchat.com" },
        ["com.linkedin.android"] = new[] { "linkedin.com" },
        ["com.github.android"] = new[] { "github.com" },
        ["tv.twitch.android.app"] = new[] { "twitch.tv" },
        ["com.valvesoftware.android.steam.community"] = new[] { "steampowered.com", "steamcommunity.com" },
        ["com.epicgames.portal"] = new[] { "epicgames.com" },
        ["com.amazon.mShop.android.shopping"] = new[] { "amazon.com" },
        ["com.alibaba.aliexpresshd"] = new[] { "aliexpress.com" },
        ["com.ebay.mobile"] = new[] { "ebay.com" },
        ["com.paypal.android.p2pmobile"] = new[] { "paypal.com" },
        ["ru.ozon.app.android"] = new[] { "ozon.ru" },
        ["com.wildberries.ru"] = new[] { "wildberries.ru" },
        ["com.avito.android"] = new[] { "avito.ru" },
        ["ru.yandex.mail"] = new[] { "yandex.ru" },
        ["ru.mail.mailapp"] = new[] { "mail.ru" },
    };

    /// <summary>Package names of browsers whose reported web domain we trust for autofill. A non-browser
    /// app is never one of these, so it cannot present a fake web domain to harvest another site's login;
    /// unknown packages are matched by their exact stored package association instead. Conservative on
    /// purpose: a browser not on the list simply degrades to manual selection, never a wrong-site fill.</summary>
    private static readonly HashSet<string> KnownBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "com.android.chrome", "com.chrome.beta", "com.chrome.dev", "com.chrome.canary",
        "org.mozilla.firefox", "org.mozilla.firefox_beta", "org.mozilla.fenix", "org.mozilla.focus",
        "com.microsoft.emmx", "com.sec.android.app.sbrowser", "com.sec.android.app.sbrowser.beta",
        "com.opera.browser", "com.opera.mini.native", "com.opera.gx", "com.brave.browser",
        "com.yandex.browser", "com.yandex.browser.beta", "com.yandex.browser.alpha",
        "com.duckduckgo.mobile.android", "com.vivaldi.browser", "com.kiwibrowser.browser",
        "com.UCMobile.intl", "com.huawei.browser", "com.mi.globalbrowser", "com.android.browser",
        "com.ecosia.android", "org.torproject.torbrowser", "com.cloudmosa.puffinFree",
    };

    /// <summary>Is this the package of a known web browser, so its reported web domain can be trusted?</summary>
    public static bool IsBrowser(string? packageName) =>
        !string.IsNullOrEmpty(packageName) && KnownBrowsers.Contains(packageName!);
}
