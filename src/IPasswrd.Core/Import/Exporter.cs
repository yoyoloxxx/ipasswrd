using System.Text;

namespace IPasswrd.Core.Import;

/// <summary>
/// Обратная дорога: сейф → CSV, который читают Chrome, Bitwarden, 1Password и наш собственный
/// импорт.
///
/// Менеджер паролей, из которого нельзя уйти, — ровно та претензия, из-за которой люди уходят
/// от предыдущего. Поэтому выход есть, и он не выборочный: карты, документы и заметки не
/// выбрасываются из-за того, что не ложатся в колонки для логинов, — их поля складываются
/// в note построчно. Вложения в текстовый файл не помещаются, и вместо молчания там остаётся
/// строка о том, сколько их и что они остались в сейфе.
///
/// Колонки — те же, что пишет экспорт Chrome (name,url,username,password,note), плюс totp
/// шестой: потерять коды проверки при переезде было бы тихой потерей.
/// </summary>
public static class Exporter
{
    public static readonly string[] Header = { "name", "url", "username", "password", "note", "totp" };

    /// <summary>Поля, которые уезжают в свои колонки и не должны дублироваться в note.</summary>
    private static readonly HashSet<string> Columned =
        new(StringComparer.Ordinal) { "url", "username", "password", "totp" };

    /// <summary>Человеческие подписи для полей карт и документов — как в карточке записи.</summary>
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["number"] = "Номер",
        ["expiry"] = "Срок",
        ["cvc"] = "CVC/CVV",
        ["holder"] = "Держатель",
        ["issued"] = "Выдан",
        ["lastName"] = "Фамилия",
        ["firstName"] = "Имя",
        ["middleName"] = "Отчество",
        ["phone"] = "Телефон",
        ["email"] = "Почта",
        ["zip"] = "Индекс",
        ["country"] = "Страна",
        ["city"] = "Город",
        ["street"] = "Адрес",
    };

    public static string ToCsv(IEnumerable<VaultEntry> entries)
    {
        var rows = new List<IReadOnlyList<string>> { Header };

        foreach (VaultEntry e in entries.OrderBy(x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (e.Item.Type == "meta") continue;   // служебная запись синхронизации, не пользовательская

            rows.Add(new[]
            {
                e.Item.Title,
                e.Item.Fields.GetValueOrDefault("url", ""),
                e.Item.Fields.GetValueOrDefault("username", ""),
                e.Item.Fields.GetValueOrDefault("password", ""),
                Note(e.Item),
                e.Item.Fields.GetValueOrDefault("totp", ""),
            });
        }

        return Csv.Write(rows);
    }

    /// <summary>
    /// Заметка записи плюс всё, чему не нашлось колонки. Порядок: сначала то, что человек
    /// написал сам, потом поля, потом напоминание про вложения.
    /// </summary>
    private static string Note(VaultItem item)
    {
        var sb = new StringBuilder(item.Notes.Trim());

        foreach (var kv in item.Fields)
        {
            if (Columned.Contains(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(Labels.GetValueOrDefault(kv.Key, kv.Key)).Append(": ").Append(kv.Value);
        }

        if (item.Folder.Length > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("Папка: ").Append(item.Folder);
        }

        if (item.Attachments.Count > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("Вложений: ").Append(item.Attachments.Count)
              .Append(" (в текстовый файл не переносятся, остались в сейфе)");
        }

        return sb.ToString();
    }
}
