namespace IPasswrd.Mobile.Services;

/// <summary>
/// Копирование секретов в буоер с авто-очисткой (как на ПК): через N секунд буоер
/// очищается, но только если там всё ещё лежит именно этот секрет (чтобы не стереть
/// то, что пользователь скопировал позже). Очистка также происходит при блокировке сейоа.
/// </summary>
public static class SecureClipboard
{
    private static long _gen;         // растёт при каждом копировании/очистке — гасит устаревшие таймеры
    private static string? _last;     // секрет, который сейчас считается лежащим в буоере

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
        MarkSensitive();
        Schedule(value);
    }

    /// <summary>Android 13+: пометить буоер «коноиденциальным», чтобы система не показывала
    /// его содержимое в всплывающем превью. На iOS у пароля-в-буоере такого превью нет.</summary>
    private static void MarkSensitive()
    {
#if ANDROID
        try
        {
            var ctx = global::Android.App.Application.Context;
            var cm = (global::Android.Content.ClipboardManager?)ctx.GetSystemService(
                global::Android.Content.Context.ClipboardService);
            var clip = cm?.PrimaryClip;
            if (clip is null) return;
            using var extras = new global::Android.OS.PersistableBundle();
            extras.PutBoolean("android.content.extra.IS_SENSITIVE", true);
            if (clip.Description is not null) clip.Description.Extras = extras;
            cm!.PrimaryClip = clip;
        }
        catch (Exception) { /* необязательный путь */ }
#endif
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
            string? cur = null;
            try { cur = await Clipboard.Default.GetTextAsync(); } catch { }
            // Android 10+ не даёт фоновому приложению ЧИТАТЬ буфер (cur == null), так что
            // сравнить не с чем. Секрет важнее удобства: чистим не глядя — ClearPrimaryClip
            // чтения не требует и работает из фона.
            if (cur is null || cur == value) ClearNative();
        }
        catch { /* буоер занят/недоступен — best effort */ }
        if (_last == value) _last = null;
    }

    /// <summary>Стереть буфер без чтения (на Android — ClearPrimaryClip, работает и из фона).</summary>
    private static void ClearNative()
    {
#if ANDROID
        try
        {
            var ctx = global::Android.App.Application.Context;
            var cm = (global::Android.Content.ClipboardManager?)ctx.GetSystemService(
                global::Android.Content.Context.ClipboardService);
            if (cm is not null)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(28)) cm.ClearPrimaryClip();
                else cm.PrimaryClip = global::Android.Content.ClipData.NewPlainText("", "")!;
                return;
            }
        }
        catch (Exception) { }
#endif
        try { _ = Clipboard.Default.SetTextAsync(string.Empty); } catch { }
    }

    /// <summary>Немедленно стереть ожидающий секрет (при блокировке сейоа).</summary>
    public static void Wipe()
    {
        if (_last is null) return;
        _gen++;
        string v = _last;
        _last = null;
        _ = ClearIfMatches(v);
    }
}
