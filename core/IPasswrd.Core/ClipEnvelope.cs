using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace IPasswrd.Core;

/// <summary>
/// Конверт для передачи буфера обмена между устройствами через облако.
///
/// Буфер ходит через папку на Google Диске — ту же, где лежит сейф. В буфере бывают пароли,
/// и класть его в облако открытым текстом нельзя, даже на минуту: файл сейфа шифруется до
/// последнего байта, а его содержимое, скопированное в буфер, вдруг лежало бы рядом голым.
///
/// Ключ — сессионный ключ сейфа (DEK): он уже есть у каждого устройства с открытым сейфом,
/// ничего согласовывать не нужно. Отсюда же честное ограничение: буфер ходит только между
/// УСТРОЙСТВАМИ С ОТКРЫТЫМ СЕЙФОМ — запертый телефон расшифровать конверт не может, и это
/// правильно: буфер — та же секретная зона, что и сейф.
///
/// Формат файла (v2): 12 байт нонса + 8 байт expiry (unix seconds, big-endian) + шифртекст
/// AES-GCM. Expiry входит в AAD, поэтому его нельзя подкрутить без ломания тега; просроченный
/// конверт отвергается (anti-replay для долго лежащих blob’ов на Диске).
/// </summary>
public static class ClipEnvelope
{
    /// <summary>Больше в буфер не кладём: это уже не «скопировал строку», а передача файла,
    /// для которой есть вложения.</summary>
    public const int MaxTextChars = 200_000;

    /// <summary>Сколько конверт живёт по умолчанию — буфер обмена не должен переживать часы.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    /// <summary>Потолок TTL при печати: дальнее «вечное» expiry — это уже replay-вектор.</summary>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(1);

    private const int ExpiryLen = 8;
    private static readonly byte[] AadPrefix = Encoding.ASCII.GetBytes("ipasswrd/clip/v2/");

    /// <summary>Запечатать текст ключом сейфа. Пустой текст и превышение предела — ошибка вызова.</summary>
    public static byte[] Seal(byte[] key, string text, TimeSpan? ttl = null)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("clip text is empty", nameof(text));
        if (text.Length > MaxTextChars) throw new ArgumentException("clip text too long", nameof(text));

        TimeSpan life = ttl ?? DefaultTtl;
        if (life <= TimeSpan.Zero || life > MaxTtl)
            throw new ArgumentOutOfRangeException(nameof(ttl), "clip TTL must be within (0, MaxTtl]");

        long expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)Math.Ceiling(life.TotalSeconds);
        byte[] aad = AadFor(expiresAt);

        byte[] nonce = Crypto.RandomBytes(Crypto.NonceLen);
        byte[] ct = Crypto.Seal(key, nonce, Encoding.UTF8.GetBytes(text), aad);

        byte[] blob = new byte[nonce.Length + ExpiryLen + ct.Length];
        nonce.CopyTo(blob, 0);
        BinaryPrimitives.WriteInt64BigEndian(blob.AsSpan(nonce.Length, ExpiryLen), expiresAt);
        ct.CopyTo(blob, nonce.Length + ExpiryLen);
        return blob;
    }

    /// <summary>
    /// Распечатать конверт. false — не наш файл, чужой ключ, порча или просрочка: для получателя
    /// это всё одно «нечего вставлять», причины различать не по чему, а падать из-за мусора в
    /// облаке точно не стоит.
    /// </summary>
    public static bool TryOpen(byte[] key, byte[] blob, out string text)
    {
        text = "";
        try
        {
            if (blob.Length <= Crypto.NonceLen + ExpiryLen + Crypto.TagLen) return false;
            byte[] nonce = blob[..Crypto.NonceLen];
            long expiresAt = BinaryPrimitives.ReadInt64BigEndian(blob.AsSpan(Crypto.NonceLen, ExpiryLen));
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (expiresAt <= now) return false; // expired (valid only while now < expiresAt)
            // Reject absurd future stamps that bypassed Seal's MaxTtl (forged / hand-edited blob).
            if (expiresAt > now + (long)MaxTtl.TotalSeconds + 60) return false;

            byte[] ct = blob[(Crypto.NonceLen + ExpiryLen)..];
            byte[] pt = Crypto.Open(key, nonce, ct, AadFor(expiresAt));
            text = Encoding.UTF8.GetString(pt);
            return text.Length > 0;
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static byte[] AadFor(long expiresAtUnix)
    {
        byte[] aad = new byte[AadPrefix.Length + ExpiryLen];
        Buffer.BlockCopy(AadPrefix, 0, aad, 0, AadPrefix.Length);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(AadPrefix.Length, ExpiryLen), expiresAtUnix);
        return aad;
    }
}
