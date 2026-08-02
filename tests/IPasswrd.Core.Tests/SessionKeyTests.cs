using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Quick unlock: reopening a vault with the exported session key (DEK), no KDF run.
public class SessionKeyTests
{
    private const string Pw = "correct horse battery staple";
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    private static VaultItem Sample() => new()
    {
        Type = "account",
        Title = "Т-Банк",
        Fields = new() { ["username"] = "u@example.com", ["password"] = "k4!Vr#92mQzL&wPn" },
    };

    [Fact]
    public void SessionKey_Reopens_Without_Password()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Sample());

        var v2 = Vault.UnlockWithSessionKey(v.Serialize(), v.ExportSessionKey());

        Assert.Equal("Т-Банк", v2.Get(id).Title);
        Assert.Equal("k4!Vr#92mQzL&wPn", v2.Get(id).Fields["password"]);
    }

    [Fact]
    public void WrongSessionKey_IsRejected()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());

        Assert.Throws<WrongMasterPasswordException>(
            () => Vault.UnlockWithSessionKey(v.Serialize(), new byte[32]));
    }

    [Fact]
    public void SessionKey_SurvivesMasterPasswordChange()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Sample());
        byte[] key = v.ExportSessionKey();

        v.ChangeMasterPassword(Pw, "another strong passphrase");   // re-wraps, DEK unchanged

        var v2 = Vault.UnlockWithSessionKey(v.Serialize(), key);
        Assert.Equal("Т-Банк", v2.Get(id).Title);
    }

    [Fact]
    public void EmptyVault_Accepts_AnyKey_ButStaysEmpty()
    {
        var v = Vault.Create(Pw, Fast);
        var v2 = Vault.UnlockWithSessionKey(v.Serialize(), v.ExportSessionKey());
        Assert.Empty(v2.Items());
    }
}
