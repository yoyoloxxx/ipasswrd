namespace IPasswrd.Mobile.Services;

/// <summary>
/// Копирование секретов в буфер с авто-очисткой (как на ПК): через N секунд буфер
/// очищается, но только если там всё ещё лежит именно этот секрет (чтобы не стереть
/// то, что пользователь скопировал позже). Очистка также происходит при блокировке сейфа.
/// </summary>
public static class SecureClipboard
{
    private static long _gen;         // растёт при каждом копировании/очистке — гасит устаревшие таймеры
    private static string? _last;     // секрет, который сейчас считается лежащим в буфере

    /// <summary>Секунд до авто-очистки (0 = выкл). Обновляется из настроек.</summary>
    public static int ClearSeconds { get; set; }

    public static readonly (string Label, int Secs)[] Options =
    {
        ("Выкл", 0), ("15 секунд", 15), ("30 секунд", 30), ("1 минута", 60), ("2 минуты", 120),
    };

    /// <summary>Скопировать секрет и завести отложенную очистку.</summary>
    public static async Task CopyAsync(string value)
    {
        await Clipboard.Default.SetTextAsync(value);
        Schedule(value);
    }

    private static async void Schedule(string value)
    {
        if (ClearSeconds <= 0 || string.IsNullOrEmpty(value)) return;
        long gen = ++_gen;
        _last = value;
        try { await Task.Delay(ClearSeconds * 1000); } catch { return; }
        if (gen != _gen) return;                 // новее копирование (или очистка) заменили это
        await ClearIfMatches(value);
    }

    private static async Task ClearIfMatches(string value)
    {
        try
        {
            string? cur = await Clipboard.Default.GetTextAsync();
            if (cur == value) await Clipboard.Default.SetTextAsync(string.Empty);
        }
        catch { /* буфер занят/недоступен — best effort */ }
        if (_last == value) _last = null;
    }

    /// <summary>Немедленно стереть ожидающий секрет (при блокировке сейфа).</summary>
    public static void Wipe()
    {
        if (_last is null) return;
        _gen++;
        string v = _last;
        _last = null;
        _ = ClearIfMatches(v);
    }
}
