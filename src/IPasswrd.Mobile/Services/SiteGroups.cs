using IPasswrd.Core;

namespace IPasswrd.Mobile.Services;

/// <summary>Группировка аккаунтов по сайту — та же логика, что на ПК:
/// ключ группы = регистрируемый домен (eTLD+1) через Core.Dedup, поэтому
/// поддомены схлопываются (passport.yandex.ru → yandex.ru), а IP не ломаются
/// (192.168.1.1 остаётся собой, а не «1.1»). Имя группы можно переопределить
/// пользовательским из meta-записи сейфа (siteNames).</summary>
public static class SiteGroups
{
    /// <summary>Фиксированный id meta-записи с синхронизируемыми настройками (тот же, что на Windows).</summary>
    public const string PrefsRecordId = "a11a5000-0000-4000-8000-000000000001";

    /// <summary>Ключ группы аккаунта: регистрируемый домен (нижним регистром); пусто → по названию.</summary>
    public static string KeyFor(VaultItem item)
    {
        string url = item.Fields.GetValueOrDefault("url", "");
        string dom = Dedup.RegistrableDomain(url);
        if (dom.Length > 0) return dom;
        return item.Title.Trim().ToLowerInvariant();
    }

    /// <summary>Отображаемое имя группы: пользовательское имя из meta или сам домен.</summary>
    public static string DisplayName(string key, Dictionary<string, string> siteNames)
    {
        if (siteNames.TryGetValue(key, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;
        return key;
    }

    /// <summary>Хост URL без www (для авто-названия новой записи).</summary>
    public static string HostOf(string url)
    {
        string s = (url ?? "").Trim();
        if (s.Length == 0) return "";
        if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return "";
        string host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return host;
    }
}
