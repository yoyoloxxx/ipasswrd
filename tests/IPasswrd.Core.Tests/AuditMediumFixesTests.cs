using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Audit medium findings M4, M5, M8, M11 (M10 covered in VaultBackupsTests).
public class AuditMediumFixesTests
{
    private const string Pw = "correct horse battery staple";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    private static VaultItem Sample(string title = "Seed", string pw = "k4!Vr#92mQzL&wPn") => new()
    {
        Type = "account",
        Title = title,
        Fields = new() { ["username"] = "u@example.com", ["password"] = pw },
    };

    private static byte[] Edit(byte[] blob, Action<JsonObject> mutate)
    {
        JsonObject root = JsonNode.Parse(blob)!.AsObject();
        mutate(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    /// <summary>Pre-vaultId legacy file: blank id and no document MAC.</summary>
    private static byte[] LegacyBlankId(byte[] sealedBlob) => Edit(sealedBlob, root =>
    {
        root["vaultId"] = "";
        root.Remove("mac");
    });

    // ---- M4: legacy blank vaultId must be stable across unlocks / devices ----

    [Fact]
    public void M4_BlankVaultId_IsStableAcrossUnlocks()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        byte[] blankId = LegacyBlankId(v.Serialize());

        var a = Vault.Unlock(blankId, Pw);
        var b = Vault.Unlock(blankId, Pw);
        var session = Vault.UnlockWithSessionKey(blankId, a.ExportSessionKey());

        Assert.False(string.IsNullOrEmpty(a.VaultId));
        Assert.Equal(a.VaultId, b.VaultId);
        Assert.Equal(a.VaultId, session.VaultId);
    }

    [Fact]
    public void M4_BlankVaultId_SurvivesPasswordChange_SameLineage()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        byte[] blankId = LegacyBlankId(v.Serialize());

        var before = Vault.Unlock(blankId, Pw);
        string id = before.VaultId;
        before.ChangeMasterPassword(Pw, "another strong passphrase");
        // Simulate another device still seeing a blank id on disk after the password change.
        byte[] afterChange = LegacyBlankId(before.Serialize());
        var after = Vault.Unlock(afterChange, "another strong passphrase");

        Assert.Equal(id, after.VaultId); // DEK unchanged ⇒ same derived lineage id
    }

    [Fact]
    public void M4_BlankVaultId_DevicesCanMerge()
    {
        var seed = Vault.Create(Pw, Fast);
        string id = seed.Add(Sample());
        byte[] blankId = LegacyBlankId(seed.Serialize());

        var phone = Vault.Unlock(blankId, Pw);
        var pc = Vault.Unlock(blankId, Pw);
        pc.Update(id, Sample("Seed", "edited-on-pc"));

        int changed = phone.MergeFrom(pc.Serialize()); // must not throw "different vault"

        Assert.Equal(1, changed);
        Assert.Equal("edited-on-pc", phone.Get(id).Fields["password"]);
    }

    // ---- M5: first poison record must not look like a wrong session key ----

    [Fact]
    public void M5_SessionKey_SkipsPoisonFirstRecord()
    {
        var v = Vault.Create(Pw, Fast);
        string goodId = v.Add(Sample("Good"));
        string poisonId = v.Add(Sample("Poison"));
        byte[] dek = v.ExportSessionKey();

        // Strip MAC so proof falls to records; put the poison record first.
        byte[] poisoned = Edit(v.Serialize(), root =>
        {
            root.Remove("mac");
            var records = root["records"]!.AsArray();
            JsonNode? poison = null;
            JsonNode? good = null;
            foreach (JsonNode? n in records)
            {
                if ((string?)n!["id"] == poisonId) poison = n;
                if ((string?)n!["id"] == goodId) good = n;
            }
            byte[] junk = new byte[Convert.FromBase64String((string)poison!["ciphertext"]!).Length];
            RandomNumberGenerator.Fill(junk);
            poison["ciphertext"] = Convert.ToBase64String(junk);
            records.Clear();
            records.Add(poison.DeepClone());
            records.Add(good!.DeepClone());
        });

        var opened = Vault.UnlockWithSessionKey(poisoned, dek);
        Assert.Equal("Good", opened.Get(goodId).Title);
    }

    [Fact]
    public void M5_SessionKey_WrongKey_StillRejected()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        Assert.Throws<WrongMasterPasswordException>(
            () => Vault.UnlockWithSessionKey(v.Serialize(), new byte[32]));
    }

    [Fact]
    public void M5_SessionKey_DistinguishesIntegrityWhenDekProved()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        byte[] dek = v.ExportSessionKey();
        // Valid records + tampered MAC: DEK proves via record, requireAuthenticated surfaces integrity.
        byte[] tampered = Edit(v.Serialize(), root =>
            root["mac"] = Convert.ToBase64String(new byte[32]));

        Assert.Throws<VaultIntegrityException>(
            () => Vault.UnlockWithSessionKey(tampered, dek, requireAuthenticated: true));
    }

    // ---- M8: sub-second UpdatedAt — later fine stamp wins without waiting a full second ----

    [Fact]
    public void M8_LaterFineStamp_WinsWithoutSecondSleep()
    {
        var seed = Vault.Create(Pw, Fast);
        string id = seed.Add(Sample());
        byte[] shared = seed.Serialize();

        var left = Vault.Unlock(shared, Pw);
        left.Update(id, Sample("Seed", "from-a"));
        byte[] leftBlob = left.Serialize();

        var right = Vault.Unlock(shared, Pw);
        // Busy-wait until the wall clock advances past left's fine stamp (<< 1s typically).
        string leftStamp = JsonNode.Parse(leftBlob)!["records"]!.AsArray()
            .First(n => (string?)n!["id"] == id)!["updatedAt"]!.GetValue<string>();
        string rightStamp;
        do
        {
            right.Update(id, Sample("Seed", "from-b"));
            rightStamp = JsonNode.Parse(right.Serialize())!["records"]!.AsArray()
                .First(n => (string?)n!["id"] == id)!["updatedAt"]!.GetValue<string>();
        } while (string.CompareOrdinal(rightStamp, leftStamp) <= 0);

        var merged = Vault.Unlock(leftBlob, Pw);
        Assert.Equal(1, merged.MergeFrom(right.Serialize()));
        Assert.Equal("from-b", merged.Get(id).Fields["password"]);
    }

    // ---- M11: recovery LWW must ignore unauthenticated peers ----

    [Fact]
    public void M11_UnauthenticatedPeer_CannotOverwriteRecovery()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        string code = v.EnableRecovery();
        // Legacy (no MAC) local that still carries a working recovery envelope.
        byte[] legacy = Edit(v.Serialize(), root => root.Remove("mac"));

        var local = Vault.Unlock(legacy, Pw);
        Assert.False(local.IsAuthenticated);
        Assert.True(local.HasRecoveryCode);

        byte[] attack = Edit(legacy, root =>
        {
            root.Remove("recovery");
            root["recoveryRevokedAt"] = "2099-01-01T00:00:00.0000000Z";
        });

        Assert.Equal(0, local.MergeFrom(attack)); // records unchanged; recovery must stay
        Assert.True(local.HasRecoveryCode);
        Assert.NotNull(Vault.UnlockWithRecoveryCode(local.Serialize(), code));
    }

    [Fact]
    public void M11_AuthenticatedPeer_StillAdoptsRecovery()
    {
        var phone = Vault.Create(Pw, Fast);
        phone.Add(Sample());
        byte[] shared = phone.Serialize();

        var pc = Vault.Unlock(shared, Pw);
        string code = pc.EnableRecovery();

        phone.MergeFrom(pc.Serialize());

        Assert.True(phone.HasRecoveryCode);
        Assert.NotNull(Vault.UnlockWithRecoveryCode(phone.Serialize(), code));
    }
}
