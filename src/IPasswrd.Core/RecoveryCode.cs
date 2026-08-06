using System.Security.Cryptography;
using System.Text;

namespace IPasswrd.Core;

/// <summary>
/// The recovery code: a 125-bit secret the user writes down once, in the shape people
/// already know how to copy off paper — five groups of five symbols:
///
///     WK3TA-9QMFH-2XVRZ-J7NBD-5PCG4
///
/// Crockford Base32 (no I, L, O, U), so reading it back is forgiving: I and L fold to 1,
/// O folds to 0, dashes and spaces are ignored, case does not matter. 125 bits is far
/// past brute force even before Argon2id is applied on top — which matters, because a
/// stolen vault file plus this code is enough to open it.
/// </summary>
public static class RecoveryCode
{
    /// <summary>Crockford Base32: 32 symbols with the ambiguous letters removed.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>25 symbols x 5 bits = 125 bits of entropy.</summary>
    public const int SymbolCount = 25;

    private const int GroupSize = 5;

    /// <summary>A fresh cryptographically random code, dashes included, ready to show once.</summary>
    public static string Generate()
    {
        var sb = new StringBuilder(SymbolCount + (SymbolCount / GroupSize) - 1);
        for (int i = 0; i < SymbolCount; i++)
        {
            if (i > 0 && i % GroupSize == 0) sb.Append('-');
            sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Fold a hand-copied code back to its canonical 25-symbol form — this, not the
    /// typed text, is what the KDF sees, so formatting never changes the derived key.
    /// Returns null when the input cannot be a recovery code at all.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (input is null) return null;

        var sb = new StringBuilder(SymbolCount);
        foreach (char raw in input)
        {
            if (raw is '-' or ' ' or '_' or '\t' or '\r' or '\n') continue;

            char c = char.ToUpperInvariant(raw);
            c = c switch { 'I' or 'L' => '1', 'O' => '0', _ => c };

            if (Alphabet.IndexOf(c) < 0) return null;   // a symbol no code can contain
            if (sb.Length == SymbolCount) return null;  // too long
            sb.Append(c);
        }
        return sb.Length == SymbolCount ? sb.ToString() : null;
    }

    /// <summary>Put the dashes back, for showing a normalized code to a human.</summary>
    public static string Format(string normalized)
    {
        var sb = new StringBuilder(normalized.Length + (normalized.Length / GroupSize));
        for (int i = 0; i < normalized.Length; i++)
        {
            if (i > 0 && i % GroupSize == 0) sb.Append('-');
            sb.Append(normalized[i]);
        }
        return sb.ToString();
    }
}
