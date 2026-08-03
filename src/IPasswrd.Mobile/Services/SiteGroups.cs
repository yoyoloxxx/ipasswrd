using IPasswrd.Core;

namespace IPasswrd.Mobile.Services;

/// <summary>Группировка аккаунтов по сайту — та же идея, что в Windows-приложении
/// (короткий домен + пользовательские имена из meta-записи сейфа).</summary>
public static class SiteGroups
{
    /// <summary>Фиксированный id meta-записи с синхронизируемыми настройками (тот же, что на Windows).</summary>
    public const string PrefsRecordId = "a11a5000-0000-4000-8000-000000000001";

    private static readonly HashSet<string> SecondLevel = new(StringComparer.OrdinalIgnoreCase)
        { "co", "com", "org", "net", "gov", "edu", "ac", "msk", "spb" };

    /// <summary>Ключ группы аккаунта: хост без www (нижним регистром); пусто → по названию.</summary>
    public static string KeyFor(VaultItem item)
    {
        string url = item.Fields.GetValueOrDefault("url", "");
        string host = HostOf(url);
        if (host.Length > 0) return host;
        return item.Title.Trim().ToLowerInvariant();
    }

    /// <summary>Отображаемое имя группы: пользовательское имя или укороченный домен.</summary>
    public static string DisplayName(string key, Dictionary<string, string> siteNames)
    {
        if (siteNames.TryGetValue(key, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;
        return Shorten(key);
    }

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

    /// <summary>"passport.yandex.ru" → "yandex.ru"; "shop.example.co.uk" → "example.co.uk".</summary>
    public static string Shorten(string host)
    {
        string[] p = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length <= 2) return host;
        bool ccSld = p[^1].Length == 2 && SecondLevel.Contains(p[^2]);
        int take = ccSld ? 3 : 2;
        return string.Join('.', p[^Math.Min(take, p.Length)..]);
    }
}
