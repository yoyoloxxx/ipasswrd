using System.Text.Json.Nodes;
using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Folders are a plain field on the record, which is the whole point: no format bump, no merge
// rule, nothing new to go wrong on sync.
public class FolderTests
{
    private const string Pw = "correct horse battery staple";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    private static VaultItem Account(string title, string folder = "") => new()
    {
        Type = "account", Title = title, Folder = folder,
        Fields = new() { ["username"] = "gleb", ["password"] = "Zq4!Nm8wRx#Ty2Lp" },
    };

    [Fact] // 1
    public void FolderSurvivesSaveAndReopen()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Account("Т-Банк", "Финансы"));

        Assert.Equal("Финансы", Vault.Unlock(v.Serialize(), Pw).Get(id).Folder);
    }

    [Fact] // 2
    public void FoldersDoNotChangeTheVaultFormat()
    {
        // Older builds carry JsonExtensionData, so an unknown field survives their edits —
        // which is exactly why this feature costs no compatibility break.
        var v = Vault.Create(Pw, Fast);
        v.Add(Account("Т-Банк", "Финансы"));

        Assert.Equal(1, (int)JsonNode.Parse(v.Serialize())!["format"]!);
    }

    [Fact] // 3
    public void FolderIsNotStoredInTheClear()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Account("Т-Банк", "Очень личное"));

        Assert.DoesNotContain("Очень личное", System.Text.Encoding.UTF8.GetString(v.Serialize()));
    }

    [Fact] // 4
    public void MovingBetweenFoldersIsJustAnEdit_AndKeepsHistory()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Account("Т-Банк", "Финансы"));

        VaultItem changed = Account("Т-Банк", "Финансы");
        changed.Fields["password"] = "новый-пароль-1";
        v.Update(id, changed);

        // Перенос идёт через ItemFolders: с появлением нескольких папок список — истина,
        // а старое поле Folder — только зеркало первой для старых сборок, писать в него напрямую нельзя.
        VaultItem moved = v.Get(id);
        ItemFolders.Set(moved, new[] { "Банки" });
        v.Update(id, moved);

        Assert.Equal("Банки", v.Get(id).Folder);
        Assert.Equal(new[] { "Банки" }, v.Get(id).Folders);
        Assert.Single(v.Get(id).History);   // перенос не считается сменой пароля
    }

    [Fact] // 5
    public void UngroupedIsTheDefault()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Account("Без папки"));

        Assert.Equal("", v.Get(id).Folder);
    }
}
