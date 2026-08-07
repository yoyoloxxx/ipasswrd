using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Папок у записи может быть несколько, а в файле при этом живут два ключа: новый список
// «folders» и старый одиночный «folder» — зеркало первой папки для сборок, которые про список
// не знают. Эти тесты держат договор между ними: список — истина, строка — зеркало, и запись,
// побывавшая на старом устройстве, не теряет ни одной папки.
public class ItemFoldersTests
{
    private const string Pw = "correct horse battery staple";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    [Fact] // 1
    public void LegacySingleFolderBecomesTheList()
    {
        var it = new VaultItem { Folder = "Работа" };

        ItemFolders.Normalize(it);

        Assert.Equal(new[] { "Работа" }, it.Folders);
        Assert.Equal("Работа", it.Folder);
    }

    [Fact] // 2
    public void TheListWinsOverTheLegacyKey()
    {
        // Так выглядит запись после правки на старой сборке: она переписала «folder»,
        // а список уцелел в Extra. Строка побеждать не может — иначе одно редактирование
        // на старом устройстве разжаловало бы запись из всех папок, кроме одной.
        var it = new VaultItem { Folder = "Работа", Folders = { "Финансы", "Семья" } };

        ItemFolders.Normalize(it);

        Assert.Equal(new[] { "Финансы", "Семья" }, it.Folders);
        Assert.Equal("Финансы", it.Folder);
    }

    [Fact] // 3
    public void MirrorIsTheFirstFolder()
    {
        var it = new VaultItem { Folders = { "Работа", "Финансы" } };

        ItemFolders.Normalize(it);

        Assert.Equal("Работа", it.Folder);
    }

    [Fact] // 4
    public void JunkIsCleanedUp()
    {
        var it = new VaultItem { Folders = { " Работа ", "", "Работа", "  " } };

        ItemFolders.Normalize(it);

        Assert.Equal(new[] { "Работа" }, it.Folders);
    }

    [Fact] // 5
    public void AddDoesNotTouchTheOthers()
    {
        var it = new VaultItem { Folders = { "Работа" } };

        ItemFolders.Add(it, "Финансы");

        Assert.Equal(new[] { "Работа", "Финансы" }, it.Folders);
        Assert.Equal("Работа", it.Folder);   // первая не сменилась — старые сборки видят запись там же
    }

    [Fact] // 6
    public void AddingTwiceIsNotAnError()
    {
        var it = new VaultItem { Folders = { "Работа" } };

        ItemFolders.Add(it, "Работа");

        Assert.Equal(new[] { "Работа" }, it.Folders);
    }

    [Fact] // 7
    public void RemoveTakesOutExactlyOne()
    {
        var it = new VaultItem { Folders = { "Работа", "Финансы", "Семья" } };

        ItemFolders.Remove(it, "Финансы");

        Assert.Equal(new[] { "Работа", "Семья" }, it.Folders);
    }

    [Fact] // 8
    public void RemovingTheFirstMovesTheMirror()
    {
        var it = new VaultItem { Folders = { "Работа", "Финансы" } };

        ItemFolders.Remove(it, "Работа");

        Assert.Equal("Финансы", it.Folder);
    }

    [Fact] // 9
    public void InChecksMembership()
    {
        var it = new VaultItem { Folders = { "Работа", "Финансы" } };

        Assert.True(ItemFolders.In(it, "Финансы"));
        Assert.False(ItemFolders.In(it, "Семья"));
        Assert.False(ItemFolders.In(it, "работа"));   // папки различаются строго, как и на ПК всегда было
    }

    // Сквозная: сейф нормализует на каждом чтении и записи, как с написанием типов.
    [Fact] // 10
    public void VaultRoundTripKeepsAllFoldersAndTheMirror()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(new VaultItem { Title = "Карта", Folders = { "Работа", "Финансы" } });

        byte[] blob = v.Serialize();
        var back = Vault.Unlock(blob, Pw).Get(id);

        Assert.Equal(new[] { "Работа", "Финансы" }, back.Folders);
        Assert.Equal("Работа", back.Folder);
    }

    // Запись, заведённая до многопапочности (только старый ключ), читается как «в одной папке».
    [Fact] // 11
    public void OldRecordsComeBackWithTheirFolder()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(new VaultItem { Title = "Почта", Folder = "Работа" });

        var back = Vault.Unlock(v.Serialize(), Pw).Get(id);

        Assert.Equal(new[] { "Работа" }, back.Folders);
    }

    [Fact] // 12
    public void SearchFindsTheRecordByAnyOfItsFolders()
    {
        var it = new VaultItem { Title = "Карта", Folders = { "Работа", "Финансы" } };

        Assert.True(ItemSearch.Matches(it, "работа"));
        Assert.True(ItemSearch.Matches(it, "финансы"));
    }

    [Fact] // 13
    public void SetReplacesTheWholeList()
    {
        var it = new VaultItem { Folders = { "Работа" } };

        ItemFolders.Set(it, new[] { "Семья", "Дача" });

        Assert.Equal(new[] { "Семья", "Дача" }, it.Folders);
        Assert.Equal("Семья", it.Folder);

        ItemFolders.Set(it, Array.Empty<string>());

        Assert.Empty(it.Folders);
        Assert.Equal("", it.Folder);
    }
}
