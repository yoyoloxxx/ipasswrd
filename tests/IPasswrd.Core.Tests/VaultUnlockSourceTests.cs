using System.Threading;
using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// C1: unlock must never imply "overwrite local with remote before open".
// Prefer local (or the newer master envelope), merge the peer afterwards.
public class VaultUnlockSourceTests
{
    private const string OldPw = "correct horse battery staple";
    private const string NewPw = "brand new master passphrase";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    private static VaultItem Sample(string title, string pw = "k4!Vr#92mQzL&wPn") => new()
    {
        Type = "account",
        Title = title,
        Fields = new() { ["username"] = "u@example.com", ["password"] = pw, ["url"] = "example.com" },
    };

    private static (byte[] local, byte[] remote) DivergedDevices(out string localOnlyId)
    {
        var seed = Vault.Create(OldPw, Fast);
        seed.Add(Sample("Seed"));
        byte[] blob = seed.Serialize();
        var phone = Vault.Unlock(blob, OldPw);
        var pc = Vault.Unlock(blob, OldPw);

        Thread.Sleep(1100);   // second-resolution UpdatedAt
        localOnlyId = phone.Add(Sample("Offline edit on phone"));
        Thread.Sleep(1100);
        pc.Add(Sample("Edit on PC"));

        return (phone.Serialize(), pc.Serialize());
    }

    [Fact]
    public void RemoteOnly_UnlocksRemote_NoMergePeer()
    {
        var v = Vault.Create(OldPw, Fast);
        byte[] remote = v.Serialize();

        var d = VaultUnlockSource.Decide(local: null, remote);

        Assert.Same(remote, d.UnlockBlob);
        Assert.Null(d.OtherBlobToMergeAfterUnlock);
        Assert.False(d.RemoteIsForeign);
    }

    [Fact]
    public void LocalOnly_UnlocksLocal_NoMergePeer()
    {
        var v = Vault.Create(OldPw, Fast);
        byte[] local = v.Serialize();

        var d = VaultUnlockSource.Decide(local, remote: null);

        Assert.Same(local, d.UnlockBlob);
        Assert.Null(d.OtherBlobToMergeAfterUnlock);
    }

    [Fact]
    public void IdenticalCopies_NoMergePeer()
    {
        var v = Vault.Create(OldPw, Fast);
        byte[] a = v.Serialize();
        byte[] b = (byte[])a.Clone();

        var d = VaultUnlockSource.Decide(a, b);

        Assert.Same(a, d.UnlockBlob);
        Assert.Null(d.OtherBlobToMergeAfterUnlock);
        Assert.False(d.RemoteIsForeign);
    }

    [Fact]
    public void SameLineage_PrefersLocal_MergesRemote_OfflineEditSurvives()
    {
        var (local, remote) = DivergedDevices(out string phoneId);

        var d = VaultUnlockSource.Decide(local, remote);

        Assert.Same(local, d.UnlockBlob);
        Assert.Same(remote, d.OtherBlobToMergeAfterUnlock);
        Assert.False(d.RemoteIsForeign);

        var opened = Vault.Unlock(d.UnlockBlob, OldPw);
        int changed = opened.MergeFrom(d.OtherBlobToMergeAfterUnlock!);

        Assert.True(changed >= 1);
        Assert.Equal("Offline edit on phone", opened.Get(phoneId).Title);
        Assert.Contains(opened.Items(), e => e.Item.Title == "Edit on PC");
    }

    [Fact]
    public void NewerRemoteEnvelope_PreferredForUnlock_LocalEditsMerged()
    {
        var seed = Vault.Create(OldPw, Fast);
        seed.Add(Sample("Seed"));
        byte[] blob = seed.Serialize();
        var phone = Vault.Unlock(blob, OldPw);
        var pc = Vault.Unlock(blob, OldPw);

        Thread.Sleep(1100);
        string phoneId = phone.Add(Sample("Phone offline"));
        Thread.Sleep(1100);
        pc.ChangeMasterPassword(OldPw, NewPw);   // newer masterChangedAt on PC / remote

        byte[] local = phone.Serialize();
        byte[] remote = pc.Serialize();

        var d = VaultUnlockSource.Decide(local, remote);

        Assert.Same(remote, d.UnlockBlob);                         // new password unlocks this side
        Assert.Same(local, d.OtherBlobToMergeAfterUnlock);

        var opened = Vault.Unlock(d.UnlockBlob, NewPw);
        Assert.Throws<WrongMasterPasswordException>(() => Vault.Unlock(d.UnlockBlob, OldPw));
        opened.MergeFrom(d.OtherBlobToMergeAfterUnlock!);

        Assert.Equal("Phone offline", opened.Get(phoneId).Title);  // offline edit kept
        Assert.NotNull(Vault.Unlock(opened.Serialize(), NewPw));   // envelope stayed the new one
    }

    [Fact]
    public void ForeignRemote_KeepsLocal_DoesNotOfferMerge()
    {
        byte[] local = Vault.Create(OldPw, Fast).Serialize();
        byte[] remote = Vault.Create(OldPw, Fast).Serialize();      // different vaultId

        var d = VaultUnlockSource.Decide(local, remote);

        Assert.Same(local, d.UnlockBlob);
        Assert.Null(d.OtherBlobToMergeAfterUnlock);
        Assert.True(d.RemoteIsForeign);
        Assert.NotEqual(Vault.VaultIdOf(local), Vault.VaultIdOf(remote));
    }

    [Fact]
    public void NeitherSide_Throws()
    {
        Assert.Throws<ArgumentException>(() => VaultUnlockSource.Decide(null, null));
        Assert.Throws<ArgumentException>(() => VaultUnlockSource.Decide(Array.Empty<byte>(), Array.Empty<byte>()));
    }

    [Fact]
    public void NewerRemoteEnvelope_StillKeepsLocalAsMergePeer()
    {
        // Regression pin for C1: even when remote envelope is newer, local is always
        // retained as the merge peer — never dropped on the floor.
        var seed = Vault.Create(OldPw, Fast);
        byte[] blob = seed.Serialize();
        var phone = Vault.Unlock(blob, OldPw);
        var pc = Vault.Unlock(blob, OldPw);
        Thread.Sleep(1100);
        phone.Add(Sample("must-not-vanish"));
        Thread.Sleep(1100);
        pc.ChangeMasterPassword(OldPw, NewPw);

        var d = VaultUnlockSource.Decide(phone.Serialize(), pc.Serialize());

        Assert.NotNull(d.OtherBlobToMergeAfterUnlock);
        var opened = Vault.Unlock(d.UnlockBlob, NewPw);
        opened.MergeFrom(d.OtherBlobToMergeAfterUnlock!);
        Assert.Contains(opened.Items(), e => e.Item.Title == "must-not-vanish");
    }
}
