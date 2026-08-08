namespace IPasswrd.Mobile.Services;

/// <summary>Одна локальная копия сейфа.</summary>
public sealed record BackupEntry(string Path, DateTime TakenUtc, long Bytes);

/// <summary>
/// Локальные снимки файла сейфа на телефоне. Снимаются ПЕРЕД тем, как локальный
/// vault.ipvault перезаписывается — привезённым из облака при синхронизации или своей
/// же новой версией при сохранении. Именно подмена файла целиком — момент, когда можно
/// потерять данные; снимок даёт откат.
///
/// Хранятся в приватной папке приложения (не в бэкапе ОС, не видны другим приложениям).
/// Держим последние <see cref="Keep"/>, старые вычищаются. Всё best-effort.
/// </summary>
public static class VaultBackups
{
    private const int Keep = 10;
    private const string DirName = "backups";
    private const string Prefix = "vault-";
    private const string Ext = ".ipvault";

    public static string BackupsDir => Path.Combine(FileSystem.AppDataDirectory, DirName);

    /// <summary>Скопировать текущий файл сейфа в папку снимков (если он есть) и вычистить старые.</summary>
    public static void Snapshot(string vaultPath)
    {
        try
        {
            if (!File.Exists(vaultPath)) return;   // ещё нечего снимать (первый запуск)
            Directory.CreateDirectory(BackupsDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            File.Copy(vaultPath, Path.Combine(BackupsDir, Prefix + stamp + Ext), overwrite: true);
            Prune();
        }
        catch (Exception) { /* снимок — подстраховка, его провал не важен */ }
    }

    /// <summary>Снимки от новых к старым. Параметр vaultPath не используется — папка одна;
    /// он оставлен, чтобы вызов читался симметрично Snapshot/Restore.</summary>
    public static IReadOnlyList<BackupEntry> List(string? vaultPath = null)
    {
        try
        {
            if (!Directory.Exists(BackupsDir)) return Array.Empty<BackupEntry>();
            return Directory.GetFiles(BackupsDir, Prefix + "*" + Ext)
                .Select(p => new FileInfo(p))
                .OrderByDescending(fi => fi.Name, StringComparer.Ordinal)
                .Select(fi => new BackupEntry(fi.FullName, fi.LastWriteTimeUtc, fi.Length))
                .ToList();
        }
        catch (Exception) { return Array.Empty<BackupEntry>(); }
    }

    /// <summary>Вернуть сейф к состоянию из копии. Текущее состояние тоже снимается копией —
    /// чтобы неудачное восстановление можно было откатить назад.</summary>
    public static void Restore(string vaultPath, string backupPath)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("Копия не найдена.");
        Snapshot(vaultPath);                       // сегодняшнее состояние — тоже в копии
        Directory.CreateDirectory(Path.GetDirectoryName(vaultPath)!);
        File.Copy(backupPath, vaultPath, overwrite: true);
    }

    private static void Prune()
    {
        try
        {
            var all = Directory.GetFiles(BackupsDir, Prefix + "*" + Ext)
                .OrderByDescending(p => p, StringComparer.Ordinal)
                .ToList();
            for (int i = Keep; i < all.Count; i++)
                try { File.Delete(all[i]); } catch (Exception) { }
        }
        catch (Exception) { }
    }
}
