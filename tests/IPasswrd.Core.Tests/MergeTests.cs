using System.Text;
using System.Text.Json.Nodes;
using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Sync merge: two copies of the same vault (same DEK lineage) diverge on separate
// devices, then are reconciled record-by-record by newest UpdatedAt, tombstones included.
public class MergeTests
{
    private const string Pw = "correct horse battery staple";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    private static VaultItem Sample(string title, string pw = "k4!Vr#92mQzL&wPn") => new()
    {
        Type = "account",
        Title = title,
        Fields = new() { ["username"] = "u@example.com", ["password"] = pw, ["url"] = "example.com" },
    };

    // Two vaults sharing one lineage (same vaultId + DEK), as if the same file opened on two devices.
    private static (Vault a, Vault b) TwoDevices(out string seedId)
    {
        var seed = Vault.Create(Pw, Fast);
        seedId = seed.Add(Sample("Seed"));
        byte[] blob = seed.Serialize();
        return (Vault.Unlock(blob, Pw), Vault.Unlock(blob, Pw));
    }

    private static byte[] SetUpdatedAt(byte[] blob, string id, string ts)
    {
        var root = JsonNode.Parse(blob)!.AsObject();
        foreach (var rec in root["records"]!.AsArray())
            if ((string?)rec!["id"] == id) rec["updatedAt"] = ts;
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    [Fact]
    public void NewRecordFromOtherDevice_IsAdded()
    {
        var (a, b) = TwoDevices(out _);
        string idY = b.Add(Sample("Added on B"));

        int changed = a.MergeFrom(b.Serialize());

        Assert.Equal(1, changed);
        Assert.Equal(2, a.Items().Count);                          // seed + Y
        Assert.Contains(a.Items(), e => e.Item.Title == "Added on B");
        Assert.Equal("Added on B", a.Get(idY).Title);
    }

    [Fact]
    public void NewerEdit_WinsAndDecrypts()
    {
        var (a, b) = TwoDevices(out string id);
        b.Update(id, Sample("Seed", "edited-on-b"));
        byte[] newer = SetUpdatedAt(b.Serialize(), id, "2999-01-01T00:00:00Z");

        int changed = a.MergeFrom(newer);

        Assert.Equal(1, changed);
        Assert.Equal("edited-on-b", a.Get(id).Fields["password"]);  // remote content, decrypts with shared DEK
    }

    [Fact]
    public void OlderEdit_IsIgnored()
    {
        var (a, b) = TwoDevices(out string id);
        b.Update(id, Sample("Seed", "edited-on-b"));
        byte[] older = SetUpdatedAt(b.Serialize(), id, "2000-01-01T00:00:00Z");

        int changed = a.MergeFrom(older);

        Assert.Equal(0, changed);
        Assert.Equal("k4!Vr#92mQzL&wPn", a.Get(id).Fields["password"]);  // local copy kept
    }

    [Fact]
    public void Deletion_PropagatesViaTombstone()
    {
        var (a, b) = TwoDevices(out string id);
        b.Delete(id);
        byte[] newer = SetUpdatedAt(b.Serialize(), id, "2999-01-01T00:00:00Z");

        a.MergeFrom(newer);

        Assert.Empty(a.Items());
        Assert.Throws<KeyNotFoundException>(() => a.Get(id));
    }

    [Fact]
    public void DifferentVault_IsRefused()
    {
        var a = Vault.Create(Pw, Fast);
        a.Add(Sample("Mine"));
        var stranger = Vault.Create(Pw, Fast);      // independent lineage → different VaultId
        stranger.Add(Sample("Theirs"));

        Assert.Throws<VaultIntegrityException>(() => a.MergeFrom(stranger.Serialize()));
    }

    [Fact]
    public void Merged_StillUnlocksWithLocalPassword_AndSurvivesRoundTrip()
    {
        var (a, b) = TwoDevices(out _);
        b.Add(Sample("From B"));
        a.MergeFrom(b.Serialize());

        var reopened = Vault.Unlock(a.Serialize(), Pw);   // local envelope preserved

        Assert.Equal(2, reopened.Items().Count);
        Assert.All(reopened.Items(), e => Assert.False(string.IsNullOrEmpty(e.Item.Title)));
    }

    [Fact]
    public void VaultId_IsStableAcrossSaveAndReopen()
    {
        var v = Vault.Create(Pw, Fast);
        string id1 = v.VaultId;
        var reopened = Vault.Unlock(v.Serialize(), Pw);
        Assert.Equal(id1, reopened.VaultId);
        Assert.False(string.IsNullOrEmpty(id1));
    }
}
