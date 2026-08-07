namespace IPasswrd.Core;

/// <summary>
/// Срок действия карты, разобранный из свободного текста.
///
/// Поле expiry человек заполняет руками и по-разному: 09/29, 9/2029, 09.29, 09-2029.
/// Разбирать приходится всё это, потому что альтернатива — молчать: истёкшая карта в сейфе
/// выглядит точно так же, как рабочая, и обнаруживается на кассе.
///
/// Карта действует до конца указанного месяца включительно — так написано на самой карте
/// и так считают банки.
/// </summary>
public static class CardExpiry
{
    public enum Status
    {
        /// <summary>Срок не заполнен или не разобрался — молчим, а не пугаем.</summary>
        Unknown,
        Ok,
        /// <summary>Истекает в ближайшее время: пора заказывать новую.</summary>
        Soon,
        Expired,
    }

    /// <summary>Последний день, когда карта ещё действует. null — разобрать не удалось.</summary>
    public static DateOnly? LastDay(string? expiry)
    {
        if (string.IsNullOrWhiteSpace(expiry)) return null;

        var digits = new List<string>();
        var cur = new System.Text.StringBuilder();
        foreach (char c in expiry)
        {
            if (char.IsDigit(c)) cur.Append(c);
            else if (cur.Length > 0) { digits.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) digits.Add(cur.ToString());

        int month, year;
        if (digits.Count >= 2)
        {
            if (!int.TryParse(digits[0], out month) || !int.TryParse(digits[1], out year)) return null;
        }
        else if (digits.Count == 1 && digits[0].Length == 4)
        {
            // «0929» без разделителя — первые две цифры месяц.
            if (!int.TryParse(digits[0][..2], out month) || !int.TryParse(digits[0][2..], out year)) return null;
        }
        else return null;

        if (month is < 1 or > 12) return null;
        if (year < 100) year += 2000;                 // 29 → 2029
        if (year is < 2000 or > 2199) return null;

        return new DateOnly(year, month, DateTime.DaysInMonth(year, month));
    }

    /// <summary>
    /// Состояние карты на указанный день. soonDays по умолчанию 60: перевыпуск занимает недели,
    /// и предупреждение за день до конца месяца бесполезно.
    /// </summary>
    public static Status Check(string? expiry, DateOnly today, int soonDays = 60)
    {
        if (LastDay(expiry) is not { } last) return Status.Unknown;
        if (today > last) return Status.Expired;
        return last <= today.AddDays(soonDays) ? Status.Soon : Status.Ok;
    }

    /// <summary>«09/2029» — единый вид для показа рядом с картой.</summary>
    public static string Format(string? expiry) =>
        LastDay(expiry) is { } d ? $"{d.Month:00}/{d.Year}" : (expiry ?? "").Trim();
}
