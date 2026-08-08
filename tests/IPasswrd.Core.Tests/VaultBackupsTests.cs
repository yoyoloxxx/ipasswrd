using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Копии файла сейфа: снимаются перед перезаписью, хранятся ограниченно, восстанавливаются
// обратимо. Тесты работают в настоящей временной папке — предмет проверки и есть файловая
// возня, подменять её нечем.
public class VaultBackupsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ipw-bk-" + Guid.NewGuid().ToString("N"));
    private string VaultPath => Path.Combine(_dir, "vault.ipvault");

    public VaultBackupsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private void WriteVault(string content) => File.WriteAllText(VaultPath, content);

    [Fact] // 1
    public void NoVaultMeansNoBackupAndNoCrash()
    {
        VaultBackups.Snapshot(VaultPath);

        Assert.Empty(VaultBackups.List(VaultPath));
    }

    [Fact] // 2
    public void SnapshotKeepsWhatWasAboutToBeOverwritten()
    {
        WriteVault("старое содержимое");
        VaultBackups.Snapshot(VaultPath);
        WriteVault("новое содержимое");

        var list = VaultBackups.List(VaultPath);
        Assert.Single(list);
        Assert.Equal("старое содержимое", File.ReadAllText(list[0].Path));
    }

    // Серия правок подряд — одна копия «до серии», а не копия на каждое сохранение.
    [Fact] // 3
    public void BackToBackSnapshotsCollapseIntoOne()
    {
        WriteVault("v1");
        VaultBackups.Snapshot(VaultPath);
        WriteVault("v2");
        VaultBackups.Snapshot(VaultPath);
        WriteVault("v3");
        VaultBackups.Snapshot(VaultPath);

        Assert.Single(VaultBackups.List(VaultPath));
    }

    [Fact] // 4
    public void RestoreBringsTheOldContentBack()
    {
        WriteVault("до беды");
        VaultBackups.Snapshot(VaultPath);
        WriteVault("после беды");

        var backup = VaultBackups.List(VaultPath)[0];
        VaultBackups.Restore(VaultPath, backup.Path);

        Assert.Equal("до беды", File.ReadAllText(VaultPath));
    }

    // Восстановление, которое нельзя откатить, — та же необратимая перезапись,
    // от которой копии и защищают.
    [Fact] // 5
    public void RestoreKeepsTodayAsABackupToo()
    {
        WriteVault("до беды");
        VaultBackups.Snapshot(VaultPath);
        WriteVault("после беды");

        var backup = VaultBackups.List(VaultPath)[0];
        VaultBackups.Restore(VaultPath, backup.Path);

        Assert.Contains(VaultBackups.List(VaultPath),
            b => File.ReadAllText(b.Path) == "после беды");
    }

    [Fact] // 6
    public void OldBackupsAreThinnedOut()
    {
        WriteVault("сейф");
        string dir = VaultBackups.DirFor(VaultPath);
        Directory.CreateDirectory(dir);

        // Двадцать дней по три копии в день, всем больше двух недель.
        DateTime start = DateTime.UtcNow.AddDays(-40);
        for (int d = 0; d < 20; d++)
            for (int h = 0; h < 3; h++)
                File.WriteAllText(Path.Combine(dir,
                    "vault-" + start.AddDays(d).AddHours(h).ToString("yyyyMMdd-HHmmss") + ".ipvault"), "старьё");

        VaultBackups.Snapshot(VaultPath);   // прогоняет чистку

        var left = VaultBackups.List(VaultPath);
        // Свежих хранится не больше KeepRecent, дневное прореживание старья не оставляет по три в день.
        Assert.True(left.Count <= VaultBackups.KeepRecent + 1,
            $"осталось {left.Count} копий — чистка не сработала");
    }

    [Fact] // 7
    public void ListIsNewestFirst()
    {
        WriteVault("сейф");
        string dir = VaultBackups.DirFor(VaultPath);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "vault-20260101-000000.ipvault"), "старая");
        File.WriteAllText(Path.Combine(dir, "vault-20260601-000000.ipvault"), "новая");

        var list = VaultBackups.List(VaultPath);

        Assert.Equal("новая", File.ReadAllText(list[0].Path));
        Assert.Equal("старая", File.ReadAllText(list[1].Path));
    }
}
