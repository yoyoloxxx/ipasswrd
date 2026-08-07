namespace IPasswrd.Core;

/// <summary>
/// Тип записи — это строка внутри файла сейфа, поэтому у неё должно быть ровно одно написание.
///
/// Однажды их стало два: Windows писал «doc», телефон — «document». Документ, заведённый на
/// iPhone, после синхронизации не попадал в раздел «Документы» на компьютере и не предлагался
/// для автозаполнения — он не терялся, но становился невидимым, что для сейфа почти одно и то же.
/// Такие расхождения нельзя чинить только в новых записях: старые уже лежат в чужих сейфах.
/// Поэтому написание приводится к общему виду при каждом чтении и записи, а не разово при
/// миграции.
/// </summary>
public static class ItemTypes
{
    public const string Account = "account";
    public const string Card = "card";
    public const string Document = "doc";
    public const string Note = "note";
    public const string Passkey = "passkey";

    /// <summary>Личные данные для форм доставки и регистрации: имя, телефон, почта, адрес.</summary>
    public const string Identity = "identity";

    /// <summary>Отдельная запись аутентификатора (живёт в разделе «Коды»).</summary>
    public const string Totp = "totp";

    /// <summary>Служебная запись с синхронизируемыми настройками — не пользовательская.</summary>
    public const string Meta = "meta";

    /// <summary>
    /// Приводит известные разночтения к каноническому виду. Незнакомый тип возвращается как
    /// есть: запись из более новой сборки должна доехать назад неиспорченной.
    /// </summary>
    public static string Normalize(string? type) => type switch
    {
        null or "" => Account,
        "document" => Document,
        "identities" or "address" => Identity,
        _ => type,
    };
}
