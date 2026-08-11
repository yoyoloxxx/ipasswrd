using IPasswrd.Core;

namespace IPasswrd.Mobile.Services;

/// <summary>Проверка паролей по базе утечек Have I Been Pwned (k-anonymity).
/// Ядро (Core.BreachCheck) не содержит сети — сюда вынесена только загрузка диапазона.
/// Наружу уходят лишь первые 5 символов SHA-1, сам пароль не передаётся.</summary>
public static class Hibp
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static async Task<string> FetchRangeAsync(string prefix)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.pwnedpasswords.com/range/" + prefix);
        req.Headers.Add("Add-Padding", "true");                 // прячет реальный результат от сети
        req.Headers.UserAgent.ParseAdd("IPasswrd-PasswordManager");
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>Проверяет уникальные пароли; возвращает словарь пароль→число утечек (только найденные).</summary>
    public static async Task<Dictionary<string, long>> CheckAsync(IEnumerable<string> passwords)
    {
        var counts = new Dictionary<string, long>();
        var rangeCache = new Dictionary<string, string>();      // prefix → body, чтобы общий префикс дёргал API один раз
        foreach (var pw in passwords.Where(p => !string.IsNullOrEmpty(p)).Distinct())
        {
            string prefix = BreachCheck.Prefix(pw);
            if (!rangeCache.TryGetValue(prefix, out var body))
            {
                body = await FetchRangeAsync(prefix);
                rangeCache[prefix] = body;
            }
            long n = BreachCheck.CountInBody(pw, body);
            if (n > 0) counts[pw] = n;
        }
        return counts;
    }

    public static string FormatCount(long n) =>
        n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.#") + " млн"
        : n >= 1_000 ? (n / 1_000.0).ToString("0.#") + " тыс."
        : n.ToString();
}
