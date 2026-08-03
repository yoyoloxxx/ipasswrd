namespace IPasswrd.Mobile.Services;

/// <summary>Мелкое форматирование для UI.</summary>
public static class Fmt
{
    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)Math.Ceiling(t.TotalDays)} дн";
        if (t.TotalHours >= 1) return $"{(int)Math.Ceiling(t.TotalHours)} ч";
        if (t.TotalMinutes >= 1) return $"{(int)Math.Ceiling(t.TotalMinutes)} мин";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))} с";
    }

    /// <summary>«1234567890123456» → «1234 5678 9012 3456».</summary>
    public static string GroupDigits(string digits)
    {
        var clean = new string(digits.Where(char.IsDigit).ToArray());
        var parts = new List<string>();
        for (int i = 0; i < clean.Length; i += 4)
            parts.Add(clean.Substring(i, Math.Min(4, clean.Length - i)));
        return string.Join(' ', parts);
    }

    /// <summary>«•••• 3456» для списка карт.</summary>
    public static string MaskCard(string number)
    {
        var clean = new string(number.Where(char.IsDigit).ToArray());
        return clean.Length >= 4 ? "•••• " + clean[^4..] : "••••";
    }

    /// <summary>«123456» → «123 456» (как в аутентификаторах).</summary>
    public static string SplitCode(string code)
        => code.Length == 6 ? code[..3] + " " + code[3..] : code.Length == 8 ? code[..4] + " " + code[4..] : code;

    /// <summary>Платёжная система по номеру. МИР (2200–2204) проверяется РАНЬШЕ Mastercard (2221–2720).</summary>
    public static string CardBrand(string number)
    {
        var d = new string(number.Where(char.IsDigit).ToArray());
        if (d.Length < 4) return "";
        int p4 = int.Parse(d[..4]);
        int p2 = int.Parse(d[..2]);
        if (p4 is >= 2200 and <= 2204) return "МИР";
        if (d[0] == '4') return "Visa";
        if (p2 is >= 51 and <= 55 || p4 is >= 2221 and <= 2720) return "Mastercard";
        if (p2 is 34 or 37) return "Amex";
        if (p2 is 35) return "JCB";
        if (p2 is 62) return "UnionPay";
        return "";
    }
}
