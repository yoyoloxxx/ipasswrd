using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Stream() exists so the iOS autofill extension does not have to hold every decrypted record —
// attachments included — in memory at once. These tests pin the two properties that make it
// usable as a drop-in for Items(): it yields the same records, and it really is lazy.
public class StreamTests
{
    private const string Pw = "correct horse battery staple";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    private static VaultItem Account(string title, string user) => new()
    {
        Type = "account",
        Title = title,
        Fields = { ["username"] = user, ["password"] = "p-" + user },
    };

    [Fact] // 1
    public void StreamYieldsTheSameRecordsAsItems()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Account("one", "a"));
        v.Add(Account("two", "b"));
        v.Add(Account("three", "c"));

        string[] fromItems = v.Items().Select(x => x.Id + "|" + x.Item.Title).OrderBy(s => s).ToArray();
        string[] fromStream = v.Stream().Select(x => x.Id + "|" + x.Item.Title).OrderBy(s => s).ToArray();

        Assert.Equal(fromItems, fromStream);
    }

    [Fact] // 2
    public void StreamSkipsDeletedRecords()
    {
        var v = Vault.Create(Pw, Fast);
        string keep = v.Add(Account("keep", "a"));
        string gone = v.Add(Account("gone", "b"));
        v.Delete(gone);

        VaultEntry only = Assert.Single(v.Stream());
        Assert.Equal(keep, only.Id);
    }

    [Fact] // 3
    public void StreamIsLazy()
    {
        var v = Vault.Create(Pw, Fast);
        for (int i = 0; i < 5; i++) v.Add(Account("n" + i, "u" + i));

        // Taking one record must not have decrypted the other four: that laziness is the whole
        // point — otherwise the extension pays for every attachment in the vault to show one login.
        int seen = 0;
        foreach (VaultEntry _ in v.Stream())
        {
            seen++;
            break;
        }

        Assert.Equal(1, seen);
        Assert.Equal(5, v.Stream().Count());
    }

    [Fact] // 4
    public void StreamedItemsAreDetachedCopies()
    {
        // The autofill path clears Attachments on what it streams to keep memory down; that must
        // not touch the vault itself, or a passport scan would vanish on the next save.
        var v = Vault.Create(Pw, Fast);
        var doc = new VaultItem { Type = "document", Title = "Паспорт" };
        doc.Attachments.Add(new Attachment
        {
            Name = "scan.jpg",
            Mime = "image/jpeg",
            Bytes = 3,
            Data = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            AddedAt = "2026-08-06T21:00:00Z",
        });
        string id = v.Add(doc);

        foreach (VaultEntry e in v.Stream()) e.Item.Attachments.Clear();

        Assert.Single(v.Get(id).Attachments);
        Assert.Single(Vault.Unlock(v.Serialize(), Pw).Get(id).Attachments);
    }
}
