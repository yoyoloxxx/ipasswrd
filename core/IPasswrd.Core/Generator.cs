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

        // Collect one character from each selected class so a short password cannot
        // accidentally omit a class the caller asked for (uniform draws from the combined
        // pool alone do not guarantee that).
        var classes = new List<string>(4);
        if (o.Lower) classes.Add(o.ExcludeAmbiguous ? "abcdefghijkmnopqrstuvwxyz" : "abcdefghijklmnopqrstuvwxyz");
        if (o.Upper) classes.Add(o.ExcludeAmbiguous ? "ABCDEFGHJKLMNPQRSTUVWXYZ" : "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        if (o.Digits) classes.Add(o.ExcludeAmbiguous ? "23456789" : "0123456789");
        if (o.Symbols) classes.Add("!@#$%^&*?-_=+");

        int reserved = Math.Min(o.Length, classes.Count);
        char[] chars = new char[o.Length];
        for (int i = 0; i < reserved; i++)
            chars[i] = classes[i][RandomNumberGenerator.GetInt32(classes[i].Length)];
        for (int i = reserved; i < o.Length; i++)
            chars[i] = pool[RandomNumberGenerator.GetInt32(pool.Length)];

        // Fisher–Yates so the reserved slots are not stuck at the front.
        for (int i = chars.Length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
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
