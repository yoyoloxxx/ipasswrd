using System.Text;

namespace IPasswrd.Core;

/// <summary>
/// Что именно ищет строка поиска в записи.
///
/// Сначала смотрели только на название, имя пользователя и адрес сайта. Для логинов этого хватало,
/// но карту по фамилии держателя или адрес по улице найти было нельзя: поиск отвечал «ничего не
/// найдено». Это худший из возможных ответов — ему верят. Человек решает, что записи нет, и заводит
/// вторую такую же; в сейфе появляется пара почти одинаковых карт, и неизвестно, какая из них
/// настоящая.
///
/// Правило живёт в ядре, а не в окне: список записей есть и на компьютере, и на телефоне, и
/// расходиться им нельзя — иначе одна и та же запись находится с ноутбука и не находится с
/// телефона, а понять почему невозможно. Заодно решение о том, что в поиск не попадает, оказывается
/// в одном месте: заводя новое поле, про него нужно вспомнить здесь, а не на двух экранах.
/// </summary>
public static class ItemSearch
{
    /// <summary>
    /// Поля, которые в поиск не попадают.
    ///
    /// Пароль и CVC исключены не ради секретности — сейф в этот момент открыт и показывает их по
    /// нажатию. Причина другая: совпадение внутри пароля выдаёт запись, которую не искали, и
    /// объяснить её появление в списке нечем — поле, по которому она нашлась, на экране не видно.
    /// Поиск, чей список результатов нельзя объяснить, перестаёт быть поиском.
    ///
    /// Служебные поля ключей доступа — машинный текст (base64, JWK). Совпасть с ним человек может
    /// только случайно, и такое совпадение — тоже необъяснимая строка в списке.
    /// </summary>
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "cvc", "totp",                             // секреты
        "privJwk", "credId", "userHandle", "alg", "keepAsIs",  // служебное
    };

    /// <summary>Попадает ли поле с таким именем в поиск.</summary>
    public static bool IsSearchable(string? key) => key is { Length: > 0 } && !Skip.Contains(key);

    /// <summary>Всё, по чему запись можно найти, одной строкой в нижнем регистре.</summary>
    public static string Text(VaultItem it)
    {
        var sb = new StringBuilder();
        Add(sb, it.Title);
        Add(sb, it.Notes);
        foreach (string folder in ItemFolders.Of(it))
            Add(sb, folder);
        foreach (var kv in it.Fields)
            if (IsSearchable(kv.Key)) Add(sb, kv.Value);
        return sb.ToString();
    }

    /// <summary>
    /// Подходит ли запись под запрос. <paramref name="extra"/> — текст, которого в самой записи нет,
    /// но который человек видит в списке: название карточки сайта, собранное из нескольких записей.
    /// Искать по тому, что написано на экране, — единственное ожидание, которое здесь есть.
    /// </summary>
    public static bool Matches(VaultItem it, string? query, string? extra = null)
        => Matches(extra is { Length: > 0 } ? extra.ToLowerInvariant() + " " + Text(it) : Text(it), query);

    /// <summary>
    /// Запрос делится на слова, и каждое должно найтись — не обязательно в одном поле и не
    /// обязательно в том же порядке.
    ///
    /// Без этого расширенный поиск был бы бесполезен ровно там, где он нужнее всего: «сбер иванов»
    /// — это название одной записи и держатель другой, целиком такая строка не встречается нигде.
    /// Порядок слов человек тоже не помнит: «иванов иван» и «иван иванов» — один и тот же человек.
    ///
    /// Слово ищется как часть слова, а не целиком: набравший «иванов» должен найти «Иванова», а
    /// «мосэнерг» — «Мосэнергосбыт». Требовать точного совпадения значит требовать, чтобы человек
    /// помнил запись, которую ищет.
    /// </summary>
    public static bool Matches(string haystack, string? query)
    {
        string q = (query ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0) return true;
        foreach (var word in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!haystack.Contains(word, StringComparison.Ordinal)) return false;
        return true;
    }

    private static void Add(StringBuilder sb, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (sb.Length > 0) sb.Append(' ');
        sb.Append(value.ToLowerInvariant());
    }
}
