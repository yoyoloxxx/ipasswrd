using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

public class AuditTests
{
    [Theory]
    [InlineData("", Strength.Weak)]
    [InlineData("abc", Strength.Weak)]              // too short
    [InlineData("password", Strength.Weak)]         // common
    [InlineData("alllowercaseletters", Strength.Weak)] // long but one class
    [InlineData("abcdefgh1", Strength.Fair)]        // len 9, two classes
    [InlineData("Summer2024", Strength.Fair)]       // len 10, three classes but < 12
    [InlineData("K#94v!nRq7$Lm", Strength.Strong)]  // len 13, four classes
    public void Rate_Classifies(string pw, Strength expected)
    {
        Assert.Equal(expected, Auditor.Rate(pw));
    }

    private static VaultEntry Acc(string id, string title, string pw)
        => new(id, new VaultItem { Type = "account", Title = title, Fields = new() { ["password"] = pw } }, "");

    [Fact]
    public void Audit_Finds_Weak_Reused_And_Ok()
    {
        var entries = new[]
        {
            Acc("1", "Ozon", "ozon123"),               // weak (short-ish, low variety)
            Acc("2", "VK", "zima2022pass"),            // reused
            Acc("3", "Steam", "zima2022pass"),         // reused (same as VK)
            Acc("4", "Bank", "K#94v!nRq7$Lm"),         // strong, unique -> ok
            new("5", new VaultItem { Type = "note", Title = "note" }, ""), // ignored (not an account)
        };

        var r = Auditor.Audit(entries);

        Assert.Equal(4, r.AccountsChecked);                       // the note is excluded
        Assert.Contains(r.Weak, f => f.Title == "Ozon");
        Assert.Equal(2, r.Reused.Count);                          // VK + Steam
        Assert.Contains(r.Reused, f => f.Title == "VK");
        Assert.Contains(r.Reused, f => f.Title == "Steam");
        Assert.Equal(1, r.Ok);                                    // only Bank
    }

    [Fact]
    public void Audit_Empty_Is_Clean()
    {
        var r = Auditor.Audit(Array.Empty<VaultEntry>());
        Assert.Equal(0, r.AccountsChecked);
        Assert.Empty(r.Weak);
        Assert.Empty(r.Reused);
    }
}
