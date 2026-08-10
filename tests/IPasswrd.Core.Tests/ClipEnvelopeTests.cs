using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

// Конверт буфера обмена: текст ходит через облако только запечатанным ключом сейфа.
public class ClipEnvelopeTests
{
    private static byte[] Key() => new byte[32];   // фиксированный ключ достаточен: проверяем конверт, не генератор

    private static byte[] OtherKey()
    {
        var k = new byte[32];
        k[0] = 1;
        return k;
    }

    [Fact] // 1
    public void RoundTrips()
    {
        byte[] blob = ClipEnvelope.Seal(Key(), "скопированный пароль: hunter2");

        Assert.True(ClipEnvelope.TryOpen(Key(), blob, out string back));
        Assert.Equal("скопированный пароль: hunter2", back);
    }

    [Fact] // 2
    public void WrongKeyOpensNothing()
    {
        byte[] blob = ClipEnvelope.Seal(Key(), "секрет");

        Assert.False(ClipEnvelope.TryOpen(OtherKey(), blob, out _));
    }

    [Fact] // 3
    public void TamperedBlobOpensNothing()
    {
        byte[] blob = ClipEnvelope.Seal(Key(), "секрет");
        blob[^1] ^= 0x01;

        Assert.False(ClipEnvelope.TryOpen(Key(), blob, out _));
    }

    [Fact] // 4
    public void GarbageOpensNothing()
    {
        Assert.False(ClipEnvelope.TryOpen(Key(), new byte[] { 1, 2, 3 }, out _));
        Assert.False(ClipEnvelope.TryOpen(Key(), new byte[64], out _));
    }

    [Fact] // 5
    public void TheCiphertextDoesNotLeakThePlaintext()
    {
        byte[] blob = ClipEnvelope.Seal(Key(), "очень секретный текст");

        Assert.DoesNotContain("секрет", System.Text.Encoding.UTF8.GetString(blob));
    }

    [Fact] // 6
    public void EmptyAndOversizedTextsAreCallerErrors()
    {
        Assert.Throws<ArgumentException>(() => ClipEnvelope.Seal(Key(), ""));
        Assert.Throws<ArgumentException>(() => ClipEnvelope.Seal(Key(), new string('x', ClipEnvelope.MaxTextChars + 1)));
    }

    // Конверт сейфовой записи и конверт буфера не взаимозаменяемы: AAD разные.
    [Fact] // 7
    public void VaultRecordsDoNotOpenAsClips()
    {
        var v = Vault.Create("correct horse battery staple", KdfConfig.Fast);
        string id = v.Add(new VaultItem { Title = "Почта", Fields = { ["password"] = "hunter2" } });
        byte[] key = v.ExportSessionKey();
        byte[] recordBlob = Convert.FromBase64String(v.RawCiphertextOf(id)!);

        Assert.False(ClipEnvelope.TryOpen(key, recordBlob, out _));
    }
}
