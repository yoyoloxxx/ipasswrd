using System.Text;
using System.Text.Json.Nodes;
using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// The document-integrity MAC (VaultDocumentDto.Mac) is what a synced copy is checked against
// before any of its clear-text metadata is trusted. These tests pin the attacks it must stop:
// a storage-level adversary — a malicious cloud or account that can rewrite the synced file but
// does NOT know the master password — trying to roll a record back to an old value, forge a
// deletion, brick the key envelope, or strip the seal to fall back to the old unauthenticated
// behaviour. The MAC key is derived from the DEK, so only a holder of the master password (or
// recovery code) can produce or verify one.
public class SyncIntegrityTests
{
    private const string Pw = "correct horse battery staple";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    private static VaultItem Sample(string title = "Site", string pw = "k4!Vr#92mQzL&wPn") => new()
    {
        Type = "account",
        Title = title,
        Fields = new() { ["username"] = "u@example.com", ["password"] = pw, ["url"] = "example.com" },
    };

    private static (Vault a, Vault b) TwoDevices(out string id)
    {
        var seed = Vault.Create(Pw, Fast);
        id = seed.Add(Sample());
        byte[] blob = seed.Serialize();
        return (Vault.Unlock(blob, Pw), Vault.Unlock(blob, Pw));
    }

    private static byte[] Edit(byte[] blob, Action<JsonObject> mutate)
    {
        JsonObject root = JsonNode.Parse(blob)!.AsObject();
        mutate(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    // ---- the seal exists and round-trips ----

    [Fact]
    public void Serialize_WritesAMac_AndUnlockAuthenticates()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        byte[] blob = v.Serialize();

        Assert.NotNull((string?)JsonNode.Parse(blob)!["mac"]);
        Assert.True(Vault.Unlock(blob, Pw).IsAuthenticated);
    }

    // ---- rollback: an old ciphertext re-stamped as the current version ----

    [Fact]
    public void ForgedUpdatedAt_OnUnlock_IsRejected()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        byte[] tampered = Edit(v.Serialize(), root =>
            root["records"]!.AsArray()[0]!["updatedAt"] = "2999-01-01T00:00:00Z");   // MAC left intact

        Assert.Throws<VaultIntegrityException>(() => Vault.Unlock(tampered, Pw));
    }

    [Fact]
    public void ForgedUpdatedAt_OnMerge_IsRejected()
    {
        var (a, b) = TwoDevices(out string id);
        byte[] tampered = Edit(b.Serialize(), root =>
        {
            foreach (var r in root["records"]!.AsArray())
                if ((string?)r!["id"] == id) r["updatedAt"] = "2999-01-01T00:00:00Z";
        });

        Assert.Throws<VaultIntegrityException>(() => a.MergeFrom(tampered));
    }

    // ---- forged deletion: a fabricated tombstone that would erase a live record ----

    [Fact]
    public void ForgedTombstone_OnMerge_IsRejected()
    {
        var (a, b) = TwoDevices(out string id);
        byte[] tombstone = Edit(b.Serialize(), root =>
        {
            foreach (var r in root["records"]!.AsArray())
                if ((string?)r!["id"] == id)
                {
                    r["deleted"] = true;
                    r["updatedAt"] = "2999-01-01T00:00:00Z";
                    r["ciphertext"] = "";
                    r["nonce"] = "";
                }
        });

        Assert.Throws<VaultIntegrityException>(() => a.MergeFrom(tombstone));
        Assert.Single(a.Items());   // the live record is untouched — the merge never began
    }

    // ---- brick: swap the key envelope so the owner's password stops working ----

    [Fact]
    public void ForgedEnvelope_OnMerge_IsRejected_AndPasswordStillWorks()
    {
        var (a, b) = TwoDevices(out _);
        // A foreign but well-formed envelope + a future stamp: last-write-wins would adopt it and brick A.
        var stranger = Vault.Create("some other master", Fast);
        JsonObject sroot = JsonNode.Parse(stranger.Serialize())!.AsObject();
        byte[] attack = Edit(b.Serialize(), root =>
        {
            root["kdf"] = sroot["kdf"]!.DeepClone();
            root["wrappedKey"] = sroot["wrappedKey"]!.DeepClone();
            root["masterChangedAt"] = "2999-01-01T00:00:00.0000000Z";
        });

        Assert.Throws<VaultIntegrityException>(() => a.MergeFrom(attack));
        Assert.NotNull(Vault.Unlock(a.Serialize(), Pw));   // a's real password is untouched
    }

    // ---- downgrade: strip the seal to make a forgery "look legacy" ----

    [Fact]
    public void StrippedMac_IsRejected_WhenAuthenticationIsRequired()
    {
        var (a, b) = TwoDevices(out string id);
        byte[] stripped = Edit(b.Serialize(), root =>
        {
            foreach (var r in root["records"]!.AsArray())
                if ((string?)r!["id"] == id) r["updatedAt"] = "2999-01-01T00:00:00Z";
            root.Remove("mac");                       // remove the seal so the forgery "looks legacy"
        });

        // A device that knows this vault is protected refuses the un-sealed copy...
        Assert.Throws<VaultIntegrityException>(() => a.MergeFrom(stripped, requireAuthenticated: true));
        // ...and so does a strict unlock of a stripped at-rest file.
        Assert.Throws<VaultIntegrityException>(() => Vault.Unlock(stripped, Pw, requireAuthenticated: true));
    }

    [Fact]
    public void MissingMac_IsToleratedByDefault_ForABareLegacyFile()
    {
        // A genuine pre-MAC file (no forgery) still opens and merges when authentication is not required.
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        byte[] legacy = Edit(v.Serialize(), root => root.Remove("mac"));

        Vault reopened = Vault.Unlock(legacy, Pw);       // requireAuthenticated defaults false
        Assert.False(reopened.IsAuthenticated);
        Assert.Single(reopened.Items());
        Assert.NotNull((string?)JsonNode.Parse(reopened.Serialize())!["mac"]);   // self-heals to authenticated on save
    }

    // ---- cross-DEK: copy the victim's vaultId but sign with a different key ----

    [Fact]
    public void ForeignBlobWithCopiedVaultId_IsRejected()
    {
        var (a, _) = TwoDevices(out _);
        var stranger = Vault.Create(Pw, Fast);
        stranger.Add(Sample("Theirs", "different"));
        byte[] impostor = Edit(stranger.Serialize(), root => root["vaultId"] = a.VaultId);  // steal the id

        Assert.Throws<VaultIntegrityException>(() => a.MergeFrom(impostor));   // MAC keyed by OUR DEK fails
    }

    // ---- corruption is detected, not silently dropped ----

    [Fact]
    public void CorruptedRecord_InAMacdFile_FailsClosed()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        byte[] corrupt = Edit(v.Serialize(), root =>
        {
            var rec = root["records"]!.AsArray()[0]!.AsObject();
            byte[] ct = Convert.FromBase64String((string)rec["ciphertext"]!);
            ct[3] ^= 0x40;
            rec["ciphertext"] = Convert.ToBase64String(ct);   // MAC still present -> mismatch, whole file refused
        });

        Assert.Throws<VaultIntegrityException>(() => Vault.Unlock(corrupt, Pw));
    }
}
