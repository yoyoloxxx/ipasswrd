using System.Security.Cryptography;
using System.Text;

namespace IPasswrd.Core;

public sealed record GeneratorOptions(
    int Length = 20,
    bool Lower = true,
    bool Upper = true,
    bool Digits = true,
    bool Symbols = true,
    bool ExcludeAmbiguous = true);

/// <summary>Cryptographically strong password generator (uses the OS CSPRNG, no modulo bias).</summary>
public static class Generator
{
    public static string Generate(GeneratorOptions? options = null)
    {
        var o = options ?? new GeneratorOptions();
        string pool = Pool(o);
        if (pool.Length == 0 || o.Length <= 0) return "";

        var sb = new StringBuilder(o.Length);
        for (int i = 0; i < o.Length; i++)
            sb.Append(pool[RandomNumberGenerator.GetInt32(pool.Length)]);   // uniform, unbiased
        return sb.ToString();
    }

    public static string Generate(int length) => Generate(new GeneratorOptions(Length: length));

    public static string Pool(GeneratorOptions o)
    {
        var sb = new StringBuilder();
        if (o.Lower) sb.Append(o.ExcludeAmbiguous ? "abcdefghijkmnopqrstuvwxyz" : "abcdefghijklmnopqrstuvwxyz");
        if (o.Upper) sb.Append(o.ExcludeAmbiguous ? "ABCDEFGHJKLMNPQRSTUVWXYZ" : "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        if (o.Digits) sb.Append(o.ExcludeAmbiguous ? "23456789" : "0123456789");
        if (o.Symbols) sb.Append("!@#$%^&*?-_=+");
        return sb.ToString();
    }
}
