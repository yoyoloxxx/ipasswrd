namespace IPasswrd.Mobile.Services;

/// <summary>
/// Локальные снимки файла сейфа на телефоне. Снимаются ПЕРЕД тем, как локальный
/// vault.ipvault перезаписывается — привезённым из облака при синхронизации или своей
/// же новой версией при сохранении. Именно подмена файла целиком (а не поэлементная
/// правка) — момент, когда можно потерять данные; снимок даёт откат.
///
/// Хранятся в приватной папке приложения (не попадает в бэкап ОС, не видна другим
/// приложениям). Держим последние <see cref="Keep"/> штук, старые вычищаются.
/// Всё best-effort: сбой снимка не должен мешать основной работе.
/// </summary>
public static class VaultBackups
{
    private const int Keep = 10;
    private const string DirName = "backups";
    private const string Prefix = "vault-";
    private const string Ext = ".ipvault";

    public static string BackupsDir =>
        Path.Combine(FileSystem.AppDataDirectory, DirName);

    /// <summary>Скопировать текущий файл сейфа в папку снимков (если он есть) и вычистить старые.</summary>
    public static void Snapshot(string vaultPath)
    {
        try
        {
            if (!File.Exists(vaultPath)) return;   // ещё нечего снимать (первый запуск)

            Directory.CreateDirectory(BackupsDir);
            // метка времени в имени — сортировка по имени совпадает с хронологией
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string dest = Path.Combine(BackupsDir, Prefix + stamp + Ext);
            File.Copy(vaultPath, dest, overwrite: true);

            Prune();
        }
        catch (Exception)
        {
            // снимок — подстраховка, его провал не важен для основной операции
        }
    }

    /// <summary>Снимки от новых к старым.</summary>
    public static IReadOnlyList<string> List()
    {
        try
        {
            if (!Directory.Exists(BackupsDir)) return Array.Empty<string>();
            return Directory.GetFiles(BackupsDir, Prefix + "*" + Ext)
                .OrderByDescending(p => p, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static void Prune()
    {
        try
        {
            var all = Directory.GetFiles(BackupsDir, Prefix + "*" + Ext)
                .OrderByDescending(p => p, StringComparer.Ordinal)
                .ToList();
            for (int i = Keep; i < all.Count; i++)
            {
                try { File.Delete(all[i]); } catch (Exception) { }
            }
        }
        catch (Exception) { }
    }
}
