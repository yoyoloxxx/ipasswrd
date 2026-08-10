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
/// Формат файла: 12 байт нонса + шифртекст AES-GCM с меткой. Своя AAD, чтобы конверт буфера
/// нельзя было подсунуть туда, где ждут запись сейфа, и наоборот.
/// </summary>
public static class ClipEnvelope
{
    /// <summary>Больше в буфер не кладём: это уже не «скопировал строку», а передача файла,
    /// для которой есть вложения.</summary>
    public const int MaxTextChars = 200_000;

    private static readonly byte[] Aad = Encoding.ASCII.GetBytes("ipasswrd/clip/v1");

    /// <summary>Запечатать текст ключом сейфа. Пустой текст и превышение предела — ошибка вызова.</summary>
    public static byte[] Seal(byte[] key, string text)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("clip text is empty", nameof(text));
        if (text.Length > MaxTextChars) throw new ArgumentException("clip text too long", nameof(text));

        byte[] nonce = Crypto.RandomBytes(Crypto.NonceLen);
        byte[] ct = Crypto.Seal(key, nonce, Encoding.UTF8.GetBytes(text), Aad);

        byte[] blob = new byte[nonce.Length + ct.Length];
        nonce.CopyTo(blob, 0);
        ct.CopyTo(blob, nonce.Length);
        return blob;
    }

    /// <summary>
    /// Распечатать конверт. false — не наш файл, чужой ключ или порча: для получателя это всё
    /// одно «нечего вставлять», причины различать не по чему, а падать из-за мусора в облаке
    /// точно не стоит.
    /// </summary>
    public static bool TryOpen(byte[] key, byte[] blob, out string text)
    {
        text = "";
        try
        {
            if (blob.Length <= Crypto.NonceLen + Crypto.TagLen) return false;
            byte[] nonce = blob[..Crypto.NonceLen];
            byte[] ct = blob[Crypto.NonceLen..];
            byte[] pt = Crypto.Open(key, nonce, ct, Aad);
            text = Encoding.UTF8.GetString(pt);
            return text.Length > 0;
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
