using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
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

    private static byte[] Mutate(byte[] blob, Action<JsonObject> edit)
    {
        JsonObject root = JsonNode.Parse(blob)!.AsObject();
        edit(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static string FlipByte(string b64, int index)
    {
        byte[] raw = Convert.FromBase64String(b64);
        raw[index] ^= 0xFF;
        return Convert.ToBase64String(raw);
    }

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

    // M9: legacy (unauthenticated) vaults may skip a poison record, but must surface the skip count.
    [Fact]
    public void LegacyListing_SkipsPoisonRecord_AndReportsSkipCount()
    {
        var v = Vault.Create(Pw, Fast);
        string good = v.Add(Account("good", "a"));
        v.Add(Account("poison", "b"));
        byte[] legacy = Mutate(v.Serialize(), root =>
        {
            // Poison the non-good record by id — JSON array order is not a stable contract.
            JsonObject poison = root["records"]!.AsArray()
                .Select(n => n!.AsObject())
                .First(r => (string?)r["id"] != good);
            poison["ciphertext"] = FlipByte((string)poison["ciphertext"]!, 5);
            root.Remove("mac");
        });

        Vault opened = Vault.Unlock(legacy, Pw);
        Assert.False(opened.IsAuthenticated);

        VaultEntry only = Assert.Single(opened.Items());
        Assert.Equal(good, only.Id);
        Assert.Equal(1, opened.LastSkippedIntegrityFailures);

        Assert.Equal(good, Assert.Single(opened.Stream()).Id);
        Assert.Equal(1, opened.LastSkippedIntegrityFailures);
    }

    // M9: authenticated vaults must fail listing closed when any live record fails decryption.
    [Fact]
    public void AuthenticatedListing_ThrowsOnPoisonRecord()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Account("good", "a"));
        string poison = v.Add(Account("poison", "b"));
        Vault sealedVault = Vault.Unlock(v.Serialize(), Pw);
        Assert.True(sealedVault.IsAuthenticated);

        // Document MAC already verified on unlock; corrupt a record in memory to exercise the
        // listing fail-closed path (MAC would have rejected this blob on disk).
        object records = typeof(Vault)
            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(sealedVault)!;
        foreach (object rec in (System.Collections.IEnumerable)records)
        {
            var idProp = rec.GetType().GetProperty("Id")!;
            if ((string)idProp.GetValue(rec)! != poison) continue;
            var ctProp = rec.GetType().GetProperty("Ciphertext")!;
            ctProp.SetValue(rec, FlipByte((string)ctProp.GetValue(rec)!, 5));
            break;
        }

        Assert.Throws<VaultIntegrityException>(() => sealedVault.Items());
        Assert.Throws<VaultIntegrityException>(() => sealedVault.Stream().ToList());
    }
}
