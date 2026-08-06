using System.Text;
using System.Text.Json.Nodes;
using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// The recovery code is the vault's second door. Each test pins one property of it:
// that it opens, that near-misses do not, that it survives the things that should not
// break it, and that it stops working the moment it is revoked.
public class RecoveryTests
{
    private const string Pw = "correct horse battery staple";
    private static readonly KdfConfig Fast = KdfConfig.Fast;   // weak on purpose: tests must stay fast

    private static VaultItem Sample() => new()
    {
        Type = "account",
        Title = "Госуслуги",
        Fields = new() { ["username"] = "gleb", ["password"] = "s3cret-Passw0rd!" },
    };

    [Fact] // 1
    public void RecoveryCode_OpensTheVaultWithoutTheMasterPassword()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Sample());
        string code = v.EnableRecovery();
        byte[] blob = v.Serialize();

        var recovered = Vault.UnlockWithRecoveryCode(blob, code);

        Assert.Equal("s3cret-Passw0rd!", recovered.Get(id).Fields["password"]);
    }

    [Fact] // 2
    public void WrongRecoveryCode_IsRejected()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        v.EnableRecovery();
        byte[] blob = v.Serialize();

        // well-formed, right length, simply not the issued code
        Assert.Throws<WrongRecoveryCodeException>(
            () => Vault.UnlockWithRecoveryCode(blob, "ZZZZZ-ZZZZZ-ZZZZZ-ZZZZZ-ZZZZZ"));
    }

    [Theory] // 3
    [InlineData("")]
    [InlineData("WK3TA-9QMFH")]                          // too short
    [InlineData("WK3TA-9QMFH-2XVRZ-J7NBD-5PCG4-EXTRA")]   // too long
    [InlineData("WK3TA-9QMFH-2XVRZ-J7NBD-5PCG!")]         // symbol outside the alphabet
    public void MalformedRecoveryCode_IsRejectedWithoutTouchingTheKdf(string bad)
    {
        var v = Vault.Create(Pw, Fast);
        v.EnableRecovery();
        byte[] blob = v.Serialize();

        Assert.Throws<WrongRecoveryCodeException>(() => Vault.UnlockWithRecoveryCode(blob, bad));
    }

    [Fact] // 4
    public void VaultWithoutRecovery_SaysSoInsteadOfLookingLikeAWrongCode()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        byte[] blob = v.Serialize();

        Assert.False(v.HasRecoveryCode);
        Assert.Throws<RecoveryNotEnabledException>(
            () => Vault.UnlockWithRecoveryCode(blob, "WK3TA-9QMFH-2XVRZ-J7NBD-5PCG4"));
    }

    [Fact] // 5
    public void CodeIsForgivingAboutHowItWasCopiedDown()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Sample());
        string code = v.EnableRecovery();
        byte[] blob = v.Serialize();

        string canonical = code.Replace("-", "");
        foreach (string typed in new[]
                 {
                     canonical,                       // no dashes at all
                     code.ToLowerInvariant(),         // lower case
                     "  " + code + "  ",              // stray whitespace
                     string.Join(" ", code.Split('-')), // spaces instead of dashes
                 })
        {
            var recovered = Vault.UnlockWithRecoveryCode(blob, typed);
            Assert.Equal("gleb", recovered.Get(id).Fields["username"]);
        }
    }

    [Fact] // 6
    public void LookAlikeLettersFoldToDigits_SoAHandCopiedCodeStillOpens()
    {
        // The alphabet has no I, L or O; someone reading 1 and 0 off paper may still write them.
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Sample());
        string code = v.EnableRecovery();
        byte[] blob = v.Serialize();

        string misread = code.Replace('1', 'I').Replace('0', 'O');
        var recovered = Vault.UnlockWithRecoveryCode(blob, misread);

        Assert.Equal("gleb", recovered.Get(id).Fields["username"]);
    }

    [Fact] // 7
    public void CodeSurvivesAMasterPasswordChange()
    {
        // Both envelopes wrap the same DEK, so re-wrapping one must not disturb the other.
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Sample());
        string code = v.EnableRecovery();
        v.ChangeMasterPassword(Pw, "a different passphrase entirely");

        var recovered = Vault.UnlockWithRecoveryCode(v.Serialize(), code);

        Assert.Equal("gleb", recovered.Get(id).Fields["username"]);
    }

    [Fact] // 8
    public void RecoveryUnlockThenReset_GivesAWorkingNewPassword()
    {
        const string newPw = "the password I will actually remember";
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Sample());
        string code = v.EnableRecovery();

        var recovered = Vault.UnlockWithRecoveryCode(v.Serialize(), code);
        recovered.ResetMasterPassword(newPw);
        byte[] blob = recovered.Serialize();

        Assert.Equal("gleb", Vault.Unlock(blob, newPw).Get(id).Fields["username"]);
        Assert.Throws<WrongMasterPasswordException>(() => Vault.Unlock(blob, Pw));
    }

    [Fact] // 9
    public void ReIssuing_RetiresThePreviousCode()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        string first = v.EnableRecovery();
        string second = v.EnableRecovery();
        byte[] blob = v.Serialize();

        Assert.NotEqual(first, second);
        Assert.Throws<WrongRecoveryCodeException>(() => Vault.UnlockWithRecoveryCode(blob, first));
        Assert.NotNull(Vault.UnlockWithRecoveryCode(blob, second));
    }

    [Fact] // 10
    public void Revoking_ClosesTheSecondDoor()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Sample());
        string code = v.EnableRecovery();
        v.DisableRecovery();
        byte[] blob = v.Serialize();

        Assert.False(v.HasRecoveryCode);
        Assert.Throws<RecoveryNotEnabledException>(() => Vault.UnlockWithRecoveryCode(blob, code));
        Assert.Equal("gleb", Vault.Unlock(blob, Pw).Get(id).Fields["username"]);   // password still works
    }

    [Fact] // 11
    public void IssuingACode_DoesNotReEncryptRecords()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Sample());
        string? before = v.RawCiphertextOf(id);

        v.EnableRecovery();

        Assert.Equal(before, v.RawCiphertextOf(id));
    }

    [Fact] // 12
    public void VaultWithoutRecovery_StaysOnTheBaselineFormat()
    {
        // Older builds must keep opening vaults that never used the feature; the moment a
        // code exists they should refuse loudly rather than silently drop it on their next save.
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        Assert.Equal(1, FormatOf(v.Serialize()));

        v.EnableRecovery();
        Assert.Equal(2, FormatOf(v.Serialize()));
    }

    [Fact] // 13
    public void RecoveryEnvelopeCannotBeSwappedIntoTheMasterSlot()
    {
        // Both envelopes wrap the same DEK. Separate AADs are what stop the recovery blob
        // from being pasted over the master one and opened with the code as a password.
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        string code = v.EnableRecovery();

        byte[] swapped = Mutate(v.Serialize(), root =>
        {
            JsonObject rec = root["recovery"]!.AsObject();
            root["kdf"] = rec["kdf"]!.DeepClone();
            root["wrappedKey"] = rec["wrappedKey"]!.DeepClone();
        });

        Assert.Throws<WrongMasterPasswordException>(() => Vault.Unlock(swapped, code));
    }

    [Fact] // 14
    public void MergeAdoptsARecoveryEnvelopeFromTheOtherDevice()
    {
        // The envelope is not a record, so it needs its own merge rule — otherwise the
        // device that has never seen the code drops it on its next save.
        var phone = Vault.Create(Pw, Fast);
        phone.Add(Sample());
        byte[] shared = phone.Serialize();

        var pc = Vault.Unlock(shared, Pw);
        string code = pc.EnableRecovery();

        phone.MergeFrom(pc.Serialize());

        Assert.True(phone.HasRecoveryCode);
        Assert.NotNull(Vault.UnlockWithRecoveryCode(phone.Serialize(), code));
    }

    [Fact] // 15
    public void MergeDoesNotResurrectARevokedCode()
    {
        var pc = Vault.Create(Pw, Fast);
        pc.Add(Sample());
        string code = pc.EnableRecovery();

        var phone = Vault.Unlock(pc.Serialize(), Pw);   // phone still carries the old envelope
        System.Threading.Thread.Sleep(1100);            // stamps are second-resolution
        pc.DisableRecovery();

        phone.MergeFrom(pc.Serialize());

        Assert.False(phone.HasRecoveryCode);
        Assert.Throws<RecoveryNotEnabledException>(
            () => Vault.UnlockWithRecoveryCode(phone.Serialize(), code));
    }

    [Fact] // 16
    public void GeneratedCodesAreDistinctAndWellFormed()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 200; i++)
        {
            string code = RecoveryCode.Generate();
            Assert.Equal("XXXXX-XXXXX-XXXXX-XXXXX-XXXXX".Length, code.Length);
            Assert.Equal(RecoveryCode.SymbolCount, RecoveryCode.Normalize(code)!.Length);
            Assert.Equal(code, RecoveryCode.Format(RecoveryCode.Normalize(code)!));
            Assert.True(seen.Add(code), "generator repeated a code");
        }
    }

    [Fact] // 17
    public void IssuedAtIsRecordedAndCleared()
    {
        var v = Vault.Create(Pw, Fast);
        Assert.Null(v.RecoveryCodeIssuedAt);

        v.EnableRecovery();
        Assert.NotNull(v.RecoveryCodeIssuedAt);
        Assert.EndsWith("Z", v.RecoveryCodeIssuedAt);

        // survives a save/load round trip so Settings can show it
        Assert.Equal(v.RecoveryCodeIssuedAt, Vault.Unlock(v.Serialize(), Pw).RecoveryCodeIssuedAt);

        v.DisableRecovery();
        Assert.Null(v.RecoveryCodeIssuedAt);
    }

    // ---- helpers ----

    private static int FormatOf(byte[] blob) => (int)JsonNode.Parse(blob)!["format"]!;

    private static byte[] Mutate(byte[] blob, Action<JsonObject> mutate)
    {
        JsonObject root = JsonNode.Parse(blob)!.AsObject();
        mutate(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }
}
