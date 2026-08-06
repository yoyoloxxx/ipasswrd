using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Losing the previous password on a change is how people get locked out of a site that
// only pretended to accept the new one. These tests pin when history is kept and when it isn't.
public class PasswordHistoryTests
{
    private const string Pw = "correct horse battery staple";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    private static VaultItem Account(string password) => new()
    {
        Type = "account",
        Title = "Госуслуги",
        Fields = new() { ["username"] = "gleb", ["password"] = password },
    };

    [Fact] // 1
    public void ChangingThePassword_KeepsTheOldOne()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Account("old-one"));

        v.Update(id, Account("new-one"));

        VaultItem item = v.Get(id);
        Assert.Equal("new-one", item.Fields["password"]);
        Assert.Single(item.History);
        Assert.Equal("old-one", item.History[0].Password);
        Assert.EndsWith("Z", item.History[0].ReplacedAt);
    }

    [Fact] // 2
    public void NewestChangeComesFirst()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Account("first"));
        v.Update(id, Account("second"));
        v.Update(id, Account("third"));

        var history = v.Get(id).History;

        Assert.Equal(new[] { "second", "first" }, history.Select(h => h.Password));
    }

    [Fact] // 3
    public void EditingSomethingElse_DoesNotAddAnEntry()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Account("same"));

        VaultItem renamed = Account("same");
        renamed.Title = "Госуслуги (личный)";
        v.Update(id, renamed);

        Assert.Empty(v.Get(id).History);
    }

    [Fact] // 4
    public void OrdinaryEdits_DoNotWipeTheHistoryTheFormNeverSaw()
    {
        // The editor rebuilds the item from input boxes and knows nothing about history;
        // it must not erase it by omission.
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Account("old-one"));
        v.Update(id, Account("new-one"));

        VaultItem renamed = Account("new-one");
        renamed.Title = "переименовано";
        v.Update(id, renamed);

        Assert.Single(v.Get(id).History);
        Assert.Equal("old-one", v.Get(id).History[0].Password);
    }

    [Fact] // 5
    public void FillingInAPasswordForTheFirstTime_IsNotAChange()
    {
        var v = Vault.Create(Pw, Fast);
        var blank = new VaultItem { Type = "account", Title = "Черновик" };
        string id = v.Add(blank);

        v.Update(id, Account("finally-a-password"));

        Assert.Empty(v.Get(id).History);
    }

    [Fact] // 6
    public void ClearingThePasswordField_DoesNotRecordAnEmptyEntry()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Account("had-one"));

        var emptied = Account("");
        v.Update(id, emptied);

        Assert.Empty(v.Get(id).History);
    }

    [Fact] // 7
    public void HistoryIsCapped_SoARecordCannotGrowForever()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Account("p0"));
        for (int i = 1; i <= Vault.MaxPasswordHistory + 5; i++)
            v.Update(id, Account("p" + i));

        var history = v.Get(id).History;

        Assert.Equal(Vault.MaxPasswordHistory, history.Count);
        Assert.Equal("p" + (Vault.MaxPasswordHistory + 4), history[0].Password);   // newest kept
        Assert.DoesNotContain(history, h => h.Password == "p0");                   // oldest dropped
    }

    [Fact] // 8
    public void ClearingHistory_LeavesTheCurrentPasswordAlone()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Account("old-one"));
        v.Update(id, Account("new-one"));

        v.ClearPasswordHistory(id);

        Assert.Empty(v.Get(id).History);
        Assert.Equal("new-one", v.Get(id).Fields["password"]);
    }

    [Fact] // 9
    public void HistorySurvivesSaveAndReopen()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Account("old-one"));
        v.Update(id, Account("new-one"));

        var reopened = Vault.Unlock(v.Serialize(), Pw);

        Assert.Equal("old-one", reopened.Get(id).History[0].Password);
    }

    [Fact] // 10
    public void CardsAndNotes_HaveNoPasswordSoNoHistory()
    {
        var v = Vault.Create(Pw, Fast);
        var card = new VaultItem { Type = "card", Title = "Т-Банк", Fields = new() { ["number"] = "4111111111111111" } };
        string id = v.Add(card);

        var edited = new VaultItem { Type = "card", Title = "Т-Банк", Fields = new() { ["number"] = "5555444433332222" } };
        v.Update(id, edited);

        Assert.Empty(v.Get(id).History);
    }

    [Fact] // 11
    public void OldPasswordsAreNotLeftInTheClear()
    {
        // History rides inside the same AES-GCM record as everything else.
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Account("old-one"));
        v.Update(id, Account("new-one"));

        string blob = System.Text.Encoding.UTF8.GetString(v.Serialize());

        Assert.DoesNotContain("old-one", blob);
        Assert.DoesNotContain("new-one", blob);
    }
}
