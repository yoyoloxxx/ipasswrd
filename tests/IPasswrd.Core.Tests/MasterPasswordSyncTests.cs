using System.Text;
using System.Text.Json.Nodes;
using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// A master-password change made on one device must reach every other copy of the vault
// through the ordinary sync merge: the envelope with the newest change stamp wins.
// (Records always merged; the envelope historically never did — the PC ended up on the
// new password while the phone quietly kept the old one, with no error on either side.
// These tests pin the fix and the compatibility story for files without the stamp.)
public class MasterPasswordSyncTests
{
    private const string OldPw = "correct horse battery staple";
    private const string NewPw = "brand new master passphrase";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    private static VaultItem Sample(string title) => new()
    {
        Type = "account",
        Title = title,
        Fields = new() { ["username"] = "u@example.com", ["password"] = "k4!Vr#92mQzL&wPn", ["url"] = "example.com" },
    };

    // Two vaults sharing one lineage (same vaultId + DEK), as if the same file opened on two devices.
    private static (Vault pc, Vault phone) TwoDevices()
    {
        var seed = Vault.Create(OldPw, Fast);
        seed.Add(Sample("Seed"));
        byte[] blob = seed.Serialize();
        return (Vault.Unlock(blob, OldPw), Vault.Unlock(blob, OldPw));
    }

    /// <summary>A file as a build that predates the stamp would write it.</summary>
    private static byte[] WithoutStamp(byte[] blob)
    {
        var root = JsonNode.Parse(blob)!.AsObject();
        root.Remove("masterChangedAt");
        root.Remove("mac");                 // a genuine pre-stamp build wrote neither field
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    [Fact]
    public void CreationStamps_AndAChangeOutranksIt()
    {
        var v = Vault.Create(OldPw, Fast);
        string created = v.MasterPasswordChangedAt;
        Assert.False(string.IsNullOrEmpty(created));            // creation is the first "change"

        v.ChangeMasterPassword(OldPw, NewPw);
        Assert.True(string.CompareOrdinal(v.MasterPasswordChangedAt, created) > 0);
    }

    [Fact]
    public void PasswordChangeOnPc_ReachesThePhoneViaMerge()
    {
        var (pc, phone) = TwoDevices();
        pc.ChangeMasterPassword(OldPw, NewPw);
        phone.Add(Sample("Edited on the phone meanwhile"));

        phone.MergeFrom(pc.Serialize());
        byte[] saved = phone.Serialize();

        var reopened = Vault.Unlock(saved, NewPw);              // the phone copy now wants the new password
        Assert.Throws<WrongMasterPasswordException>(() => Vault.Unlock(saved, OldPw));
        Assert.Equal(2, reopened.Items().Count);                // and the phone's own edit survived
    }

    [Fact]
    public void OpenSession_KeepsWorkingAfterAdoptingTheEnvelope()
    {
        var (pc, phone) = TwoDevices();
        pc.ChangeMasterPassword(OldPw, NewPw);
        phone.MergeFrom(pc.Serialize());

        string id = phone.Add(Sample("Added after adoption"));  // same DEK: the session never notices
        Assert.Equal("Added after adoption", phone.Get(id).Title);
        Assert.Equal("Added after adoption", Vault.Unlock(phone.Serialize(), NewPw).Get(id).Title);
    }

    [Fact]
    public void StaleEnvelope_DoesNotRollBackANewerPassword()
    {
        var (pc, phone) = TwoDevices();
        byte[] phoneStale = phone.Serialize();                  // still the creation-time envelope
        pc.ChangeMasterPassword(OldPw, NewPw);

        pc.MergeFrom(phoneStale);                               // the stale copy arrives after the change

        Assert.NotNull(Vault.Unlock(pc.Serialize(), NewPw));    // the newer change stands
        Assert.Throws<WrongMasterPasswordException>(() => Vault.Unlock(pc.Serialize(), OldPw));
    }

    [Fact]
    public void TwoConcurrentChanges_NewestWins_OlderIsIgnored()
    {
        var (a, b) = TwoDevices();
        b.ChangeMasterPassword(OldPw, "passphrase set on device B");   // older change first
        a.ChangeMasterPassword(OldPw, "passphrase set on device A");   // later in real time -> newer stamp
        byte[] aBlob = a.Serialize();                          // A decisively newer, MAC valid
        byte[] bBlob = b.Serialize();                          // B's change, before it ever sees A's

        b.MergeFrom(aBlob);                                     // newer stamp arrives → adopt
        Assert.NotNull(Vault.Unlock(b.Serialize(), "passphrase set on device A"));

        var a2 = Vault.Unlock(aBlob, "passphrase set on device A");
        a2.MergeFrom(bBlob);                                    // B's change is older → keep A's
        Assert.NotNull(Vault.Unlock(a2.Serialize(), "passphrase set on device A"));
    }

    [Fact]
    public void LegacyFileWithoutStamp_AdoptsAStampedChange()
    {
        var (pc, phone) = TwoDevices();
        pc.ChangeMasterPassword(OldPw, NewPw);
        var phoneLegacy = Vault.Unlock(WithoutStamp(phone.Serialize()), OldPw);   // old build's file

        phoneLegacy.MergeFrom(pc.Serialize());

        Assert.NotNull(Vault.Unlock(phoneLegacy.Serialize(), NewPw));
    }

    [Fact]
    public void TwoLegacyFiles_KeepTheLocalEnvelope_AsEveryOldBuildDid()
    {
        var (a, b) = TwoDevices();
        var aLegacy = Vault.Unlock(WithoutStamp(a.Serialize()), OldPw);
        b.ChangeMasterPassword(OldPw, NewPw);
        byte[] bLegacy = WithoutStamp(b.Serialize());           // the changing side dropped the stamp too

        aLegacy.MergeFrom(bLegacy);

        Assert.NotNull(Vault.Unlock(aLegacy.Serialize(), OldPw));   // pre-stamp behaviour, unchanged
    }

    [Fact]
    public void RecoveryCode_SurvivesEnvelopeAdoption()
    {
        var (pc, phone) = TwoDevices();
        string code = pc.EnableRecovery();
        pc.ChangeMasterPassword(OldPw, NewPw);

        phone.MergeFrom(pc.Serialize());

        var viaCode = Vault.UnlockWithRecoveryCode(phone.Serialize(), code);   // second door intact
        Assert.Single(viaCode.Items());
    }

    [Fact]
    public void QuickUnlockSessionKey_StillOpensAfterAdoption()
    {
        var (pc, phone) = TwoDevices();
        byte[] dek = phone.ExportSessionKey();                  // what Face ID / fingerprint stores
        pc.ChangeMasterPassword(OldPw, NewPw);
        phone.MergeFrom(pc.Serialize());

        var quick = Vault.UnlockWithSessionKey(phone.Serialize(), dek);   // biometrics unaffected
        Assert.Single(quick.Items());
    }

    [Fact]
    public void DamagedEnvelope_IsNotAdopted_RecordsStillMerge()
    {
        var (pc, phone) = TwoDevices();
        pc.ChangeMasterPassword(OldPw, NewPw);
        pc.Add(Sample("Added on PC"));
        var root = JsonNode.Parse(pc.Serialize())!.AsObject();
        root.Remove("mac");                                    // a legacy peer with no integrity seal...
        root["kdf"]!["salt"] = "*** not base64 ***";            // ...and a broken wrapping must never replace a working one
        byte[] damaged = Encoding.UTF8.GetBytes(root.ToJsonString());

        int changed = phone.MergeFrom(damaged);                 // requireAuthenticated defaults false -> legacy tolerated

        Assert.Equal(1, changed);                               // the record still arrived
        Assert.NotNull(Vault.Unlock(phone.Serialize(), OldPw)); // envelope stayed local
    }

    [Fact]
    public void Stamp_IsReadableWithoutThePassword()
    {
        var v = Vault.Create(OldPw, Fast);
        v.ChangeMasterPassword(OldPw, NewPw);

        Assert.Equal(v.MasterPasswordChangedAt, Vault.MasterPasswordChangedAtOf(v.Serialize()));
        Assert.Equal("", Vault.MasterPasswordChangedAtOf(WithoutStamp(v.Serialize())));
    }

    [Fact]
    public void Stamp_SurvivesSaveAndReopen()
    {
        var v = Vault.Create(OldPw, Fast);
        v.ChangeMasterPassword(OldPw, NewPw);
        string at = v.MasterPasswordChangedAt;

        var reopened = Vault.Unlock(v.Serialize(), NewPw);

        Assert.Equal(at, reopened.MasterPasswordChangedAt);
        Assert.Equal(at, Vault.Unlock(reopened.Serialize(), NewPw).MasterPasswordChangedAt);
    }
}
