using System.Text;
using System.Text.Json.Nodes;
using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Mirrors reference/test_vault_reference.py: each test pins one security property.
public class VaultTests
{
    private const string Pw = "correct horse battery staple";
    // deliberately weak KDF so tests run fast; real vaults use KdfConfig.Default (64 MiB)
    private static readonly KdfConfig Fast = KdfConfig.Fast;

    private static VaultItem Sample() => new()
    {
        Type = "account",
        Title = "Т-Банк",
        Fields = new()
        {
            ["username"] = "gleb.hse@gmail.com",
            ["password"] = "k4!Vr#92mQzL&wPn",
            ["url"] = "tbank.ru",
            ["totp"] = "otpauth://...",
        },
        Notes = "основной аккаунт",
        Favorite = true,
    };

    [Fact] // 1
    public void RoundTrip_Create_Serialize_Unlock_Read()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Sample());
        byte[] blob = v.Serialize();

        var v2 = Vault.Unlock(blob, Pw);
        VaultItem item = v2.Get(id);

        Assert.Equal("Т-Банк", item.Title);
        Assert.Equal("k4!Vr#92mQzL&wPn", item.Fields["password"]);
        Assert.True(item.Favorite);
    }

    [Fact] // 2
    public void WrongMasterPassword_IsRejected()
    {
        var v = Vault.Create(Pw, Fast);
        v.Add(Sample());
        byte[] blob = v.Serialize();

        Assert.Throws<WrongMasterPasswordException>(() => Vault.Unlock(blob, "wrong password"));
    }

    [Fact] // 3
    public void TamperedRecord_IsDetected()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Sample());
        byte[] tampered = Mutate(v.Serialize(), root =>
        {
            var rec = root["records"]!.AsArray()[0]!.AsObject();
            rec["ciphertext"] = FlipByte((string)rec["ciphertext"]!, 5);
        });

        var v2 = Vault.Unlock(tampered, Pw);
        Assert.Throws<VaultIntegrityException>(() => v2.Get(id));
    }

    [Fact] // 4
    public void TamperedWrappedKey_LooksLikeWrongPassword()
    {
        var v = Vault.Create(Pw, Fast);
        byte[] tampered = Mutate(v.Serialize(), root =>
        {
            var wk = root["wrappedKey"]!.AsObject();
            wk["ciphertext"] = FlipByte((string)wk["ciphertext"]!, 0);
        });

        Assert.Throws<WrongMasterPasswordException>(() => Vault.Unlock(tampered, Pw));
    }

    [Fact] // 5
    public void ChangeMasterPassword_RewrapsKeyOnly()
    {
        const string newPw = "new stronger passphrase 2026";
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Sample());
        string? ctBefore = v.RawCiphertextOf(id);

        v.ChangeMasterPassword(Pw, newPw);
        byte[] blob = v.Serialize();

        Assert.Equal(ctBefore, v.RawCiphertextOf(id));                     // records untouched
        Assert.Throws<WrongMasterPasswordException>(() => Vault.Unlock(blob, Pw)); // old dead
        var v2 = Vault.Unlock(blob, newPw);                               // new works
        Assert.Equal("gleb.hse@gmail.com", v2.Get(id).Fields["username"]);

        Assert.Throws<WrongMasterPasswordException>(() => v2.ChangeMasterPassword("not the old one", "x"));
    }

    [Fact] // 6
    public void RecordIsBoundToItsId_NoSwap()
    {
        var v = Vault.Create(Pw, Fast);
        string a = v.Add(new VaultItem { Type = "account", Title = "A", Fields = new() { ["password"] = "aaa" } });
        string b = v.Add(new VaultItem { Type = "account", Title = "B", Fields = new() { ["password"] = "bbb" } });

        byte[] swapped = Mutate(v.Serialize(), root =>
        {
            var recs = root["records"]!.AsArray();
            var r0 = recs[0]!.AsObject();
            var r1 = recs[1]!.AsObject();
            string c0 = (string)r0["ciphertext"]!, c1 = (string)r1["ciphertext"]!;
            string n0 = (string)r0["nonce"]!, n1 = (string)r1["nonce"]!;
            r0["ciphertext"] = c1; r1["ciphertext"] = c0;   // swap payloads, keep ids
            r0["nonce"] = n1; r1["nonce"] = n0;
        });

        var v2 = Vault.Unlock(swapped, Pw);
        Assert.Throws<VaultIntegrityException>(() => v2.Get(a));
        Assert.Throws<VaultIntegrityException>(() => v2.Get(b));
    }

    [Fact] // 7
    public void EachVaultGetsAFreshSalt()
    {
        JsonObject A = JsonNode.Parse(Vault.Create(Pw, Fast).Serialize())!.AsObject();
        JsonObject B = JsonNode.Parse(Vault.Create(Pw, Fast).Serialize())!.AsObject();

        Assert.NotEqual((string)A["kdf"]!["salt"]!, (string)B["kdf"]!["salt"]!);
        Assert.NotEqual((string)A["wrappedKey"]!["ciphertext"]!, (string)B["wrappedKey"]!["ciphertext"]!);
    }

    [Fact] // 8
    public void IdenticalPlaintext_YieldsDifferentCiphertext()
    {
        var v = Vault.Create(Pw, Fast);
        string a = v.Add(Sample());
        string b = v.Add(Sample());

        Assert.NotEqual(v.RawCiphertextOf(a), v.RawCiphertextOf(b));
    }

    [Fact] // 9
    public void Delete_Tombstones_And_Hides()
    {
        var v = Vault.Create(Pw, Fast);
        string id = v.Add(Sample());
        v.Delete(id);

        Assert.DoesNotContain(v.Items(), e => e.Id == id);
        Assert.Throws<KeyNotFoundException>(() => v.Get(id));
    }

    // ---- helpers ----

    private static byte[] Mutate(byte[] blob, Action<JsonObject> mutate)
    {
        JsonObject root = JsonNode.Parse(blob)!.AsObject();
        mutate(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static string FlipByte(string base64, int index)
    {
        byte[] b = Convert.FromBase64String(base64);
        b[index] ^= 0x01;
        return Convert.ToBase64String(b);
    }
}
