namespace IPasswrd.Core;

/// <summary>
/// Резервные копии файла сейфа — на диске, рядом с ним, в подпапке backups.
///
/// Сейф перезаписывается целиком при каждом изменении, и любая ошибка — в программе,
/// в синхронизации, на диске — до сих пор была необратимой: старого файла больше нет.
/// Однажды так почти пропали вложения; спасла только история версий на Google Диске,
/// которой могло и не быть. Своя копия перед каждой перезаписью снимает этот класс
/// потерь целиком, и не только на устройствах с облаком.
///
/// Копия — тот же зашифрованный блоб: мастер-пароль нужен для чтения копии так же, как
/// для сейфа, поэтому папка backups не секретнее самого сейфа.
///
/// Хранится немного: свежие копии плюс по одной за день. Копия каждые пять минут при
/// активной правке бессмысленна — важен снимок «до того, как всё пошло не так», а не
/// каждое сохранение. Отдельной настройки «включить копии» нет: защита, которую можно
/// забыть включить, срабатывает ровно у тех, кому была нужнее всех.
/// </summary>
public static class VaultBackups
{
    /// <summary>Свежих копий, которые хранятся всегда, сколь угодно частых.</summary>
    public const int KeepRecent = 8;

    /// <summary>Дней, за которые хранится по одной (последней) копии.</summary>
    public const int KeepDays = 14;

    /// <summary>Чаще этого новые копии не создаются: серия правок подряд — одна копия «до серии».</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    public static string DirFor(string vaultPath) =>
        Path.Combine(Path.GetDirectoryName(vaultPath) ?? ".", "backups");

    /// <summary>
    /// Снять копию ПЕРЕД перезаписью сейфа. Молча ничего не делает, если сейфа ещё нет,
    /// копия снималась только что или диск отказал: резерв не должен ронять сохранение.
    /// </summary>
    /// <param name="force">
    /// When true, ignore <see cref="MinInterval"/>. Use before a destructive replace
    /// (cloud pull that would overwrite local bytes, restore, merge-before-write on unlock)
    /// so a recent ordinary save cannot suppress the only rollback copy of unsynced edits.
    /// </param>
    public static void Snapshot(string vaultPath, bool force = false)
    {
        try
        {
            if (!File.Exists(vaultPath)) return;

            string dir = DirFor(vaultPath);
            Directory.CreateDirectory(dir);

            var existing = List(vaultPath);
            if (!force && existing.Count > 0 && DateTime.UtcNow - existing[0].TakenUtc < MinInterval) return;

            // Force may re-snapshot within the same second as an earlier copy — disambiguate
            // the filename so we never overwrite the previous forced snapshot in place.
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string name = force
                ? "vault-" + stamp + "-force.ipvault"
                : "vault-" + stamp + ".ipvault";
            File.Copy(vaultPath, Path.Combine(dir, name), overwrite: true);

            Prune(vaultPath);
        }
        catch { /* best effort — сохранение сейфа важнее копии */ }
    }

    public sealed record Backup(string Path, DateTime TakenUtc, long Bytes);

    /// <summary>Копии, свежие первыми.</summary>
    public static List<Backup> List(string vaultPath)
    {
        var list = new List<Backup>();
        try
        {
            string dir = DirFor(vaultPath);
            if (!Directory.Exists(dir)) return list;
            foreach (string f in Directory.GetFiles(dir, "vault-*.ipvault"))
            {
                var fi = new FileInfo(f);
                list.Add(new Backup(f, TakenAt(f, fi), fi.Length));
            }
            list.Sort((a, b) => b.TakenUtc.CompareTo(a.TakenUtc));
        }
        catch { /* нет копий — нет списка */ }
        return list;
    }

    /// <summary>
    /// Вернуть сейф к копии. Текущий файл сам становится копией прямо перед этим:
    /// восстановление, которое нельзя откатить, — это та же необратимая перезапись,
    /// от которой копии и защищают.
    /// <para>
    /// <paramref name="backupPath"/> must resolve inside this vault's backup directory and
    /// match the <c>vault-*.ipvault</c> name pattern; the file must also parse as a vault
    /// document before the live file is replaced. Arbitrary paths are refused so a malicious
    /// or confused caller cannot overwrite the vault with an unrelated file.
    /// </para>
    /// </summary>
    public static void Restore(string vaultPath, string backupPath)
    {
        string dir = DirFor(vaultPath);
        string fullVault = Path.GetFullPath(vaultPath);
        string fullDir = Path.GetFullPath(dir);
        string fullBackup = Path.GetFullPath(backupPath);

        string dirPrefix = fullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
        if (!fullBackup.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("backup path must be inside the vault backups directory", nameof(backupPath));

        string name = Path.GetFileName(fullBackup);
        if (name.Length == 0
            || !name.StartsWith("vault-", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".ipvault", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("backup name must match vault-*.ipvault", nameof(backupPath));

        if (!File.Exists(fullBackup))
            throw new FileNotFoundException("backup file not found", fullBackup);

        // Parse before replacing the live vault — refuse non-vault bytes.
        Vault.EnsureDocument(File.ReadAllBytes(fullBackup));

        // Не Snapshot(): у него защита от частых копий, а здесь копия обязана сняться.
        Directory.CreateDirectory(fullDir);
        if (File.Exists(fullVault))
            File.Copy(fullVault, Path.Combine(fullDir,
                "vault-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-до-восстановления.ipvault"), overwrite: true);

        File.Copy(fullBackup, fullVault, overwrite: true);
        Prune(fullVault);
    }

    /// <summary>Свежие — все (до KeepRecent), старше — по последней за день, за KeepDays дней.</summary>
    private static void Prune(string vaultPath)
    {
        var all = List(vaultPath);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var b in all.Take(KeepRecent)) keep.Add(b.Path);

        DateTime floor = DateTime.UtcNow.Date.AddDays(-KeepDays);
        foreach (var day in all.Where(b => b.TakenUtc >= floor).GroupBy(b => b.TakenUtc.Date))
            keep.Add(day.OrderByDescending(b => b.TakenUtc).First().Path);

        foreach (var b in all)
            if (!keep.Contains(b.Path))
                try { File.Delete(b.Path); } catch { /* займёмся в следующий раз */ }
    }

    private static DateTime TakenAt(string path, FileInfo fi)
    {
        // Время — из имени файла: оно не сбивается копированием между дисками.
        string stem = Path.GetFileNameWithoutExtension(path);
        int i = stem.IndexOf('-');
        if (i >= 0 && stem.Length >= i + 16
            && DateTime.TryParseExact(stem.Substring(i + 1, 15), "yyyyMMdd-HHmmss", null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out DateTime t))
            return t;
        return fi.LastWriteTimeUtc;
    }
}
