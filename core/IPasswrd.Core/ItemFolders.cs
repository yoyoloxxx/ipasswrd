namespace IPasswrd.Core;

/// <summary>
/// Папки записи. Их может быть несколько: рабочая карта — это и «Работа», и «Финансы», и выбор
/// «куда положить» из двух правильных ответов сам по себе ошибка.
///
/// В файле живут два ключа. Новый — «folders», список. Старый — «folder», одна строка: сборки до
/// многопапочности читают только его, и запись обязана оставаться видимой в своей первой папке
/// и на не обновлённом устройстве. Поэтому при каждом чтении и записи список и строка сводятся
/// к одному виду: список — истина, строка — зеркало первой папки. Разово мигрировать нельзя по
/// той же причине, что и с написанием типов: старые записи уже лежат в чужих сейфах.
///
/// Старая сборка, редактируя запись, перезапишет «folder» и не тронет «folders» — тот уедет в
/// Extra и вернётся невредимым. Расхождение (первая папка списка ≠ строке) чинится здесь же при
/// следующем чтении: строка побеждать не может, иначе одно редактирование на старом устройстве
/// разжаловало бы запись из всех папок, кроме одной.
/// </summary>
public static class ItemFolders
{
    /// <summary>Свести «folder» и «folders» к одному виду. Вызывается сейфом на каждом чтении и записи.</summary>
    public static void Normalize(VaultItem it)
    {
        var list = Clean(it.Folders);
        if (list.Count == 0 && it.Folder.Trim() is { Length: > 0 } single) list.Add(single);
        it.Folders = list;
        it.Folder = list.Count > 0 ? list[0] : "";
    }

    /// <summary>Папки записи, не полагаясь на то, что её уже нормализовали (форма могла собрать её только что).</summary>
    public static IReadOnlyList<string> Of(VaultItem it)
    {
        var list = Clean(it.Folders);
        if (list.Count == 0 && it.Folder.Trim() is { Length: > 0 } single) list.Add(single);
        return list;
    }

    public static bool In(VaultItem it, string folder) =>
        Of(it).Contains(folder, StringComparer.Ordinal);

    /// <summary>Положить в папку, не забирая из остальных. Повторное добавление — не ошибка, а «уже там».</summary>
    public static void Add(VaultItem it, string folder)
    {
        folder = folder.Trim();
        if (folder.Length == 0) return;
        var list = new List<string>(Of(it));
        if (!list.Contains(folder, StringComparer.Ordinal)) list.Add(folder);
        Set(it, list);
    }

    /// <summary>Убрать из одной папки; в остальных запись остаётся.</summary>
    public static void Remove(VaultItem it, string folder) =>
        Set(it, Of(it).Where(f => !string.Equals(f, folder, StringComparison.Ordinal)));

    /// <summary>Задать список целиком (форма редактирования владеет всем списком сразу).</summary>
    public static void Set(VaultItem it, IEnumerable<string> folders)
    {
        it.Folders = Clean(folders);
        it.Folder = it.Folders.Count > 0 ? it.Folders[0] : "";
    }

    private static List<string> Clean(IEnumerable<string>? folders)
    {
        var list = new List<string>();
        if (folders is null) return list;
        foreach (string raw in folders)
        {
            string f = (raw ?? "").Trim();
            if (f.Length > 0 && !list.Contains(f, StringComparer.Ordinal)) list.Add(f);
        }
        return list;
    }
}
