using System.Security.Cryptography;
using System.Text;

namespace IPasswrd.Core;

/// <summary>Time-based one-time passwords (RFC 6238 / HOTP RFC 4226). Verification codes
/// are computed locally from a shared secret; no network, no stored code.</summary>
public sealed record TotpConfig(string Secret, int Digits, int Period, string Algorithm, string Label,
                                string Issuer = "", string Account = "")
{
    public string Now() => Totp.Generate(Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Digits, Period, Algorithm);
    public int SecondsRemaining() => Totp.SecondsRemaining(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Period);
}

public static class Totp
{
    /// <summary>Accepts a raw Base32 secret or a full <c>otpauth://totp/...</c> URI.</summary>
    public static TotpConfig Parse(string input)
    {
        input = (input ?? "").Trim();
        if (input.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(input);
            var q = ParseQuery(uri.Query);
            string secret = q.GetValueOrDefault("secret", "");
            int digits = int.TryParse(q.GetValueOrDefault("digits", "6"), out var d) ? d : 6;
            int period = int.TryParse(q.GetValueOrDefault("period", "30"), out var p) ? p : 30;
            string algo = q.GetValueOrDefault("algorithm", "SHA1");
            string label = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));

            // Label is conventionally "Issuer:Account" (issuer may also be a separate query param).
            string issuer = q.GetValueOrDefault("issuer", "");
            string account = label;
            int sep = label.IndexOf(':');
            if (sep >= 0)
            {
                if (issuer.Length == 0) issuer = label[..sep].Trim();
                account = label[(sep + 1)..].Trim();
            }
            return new TotpConfig(secret, digits, period, algo, label, issuer, account);
        }
        return new TotpConfig(input.Replace(" ", ""), 6, 30, "SHA1", "");
    }

    /// <summary>True if <paramref name="secretOrUri"/> yields a usable code — a decodable Base32
    /// secret or a well-formed <c>otpauth://</c> URI. Used to validate manual entry before saving.</summary>
    public static bool IsValidSecret(string secretOrUri)
    {
        try
        {
            var c = Parse(secretOrUri);
            if (string.IsNullOrWhiteSpace(c.Secret)) return false;
            if (Base32Decode(c.Secret).Length == 0) return false;
            _ = Generate(c.Secret, 0, c.Digits, c.Period, c.Algorithm);
            return true;
        }
        catch { return false; }
    }

    public static string GenerateFrom(string secretOrUri, long unixSeconds)
    {
        var c = Parse(secretOrUri);
        return Generate(c.Secret, unixSeconds, c.Digits, c.Period, c.Algorithm);
    }

    public static string Generate(string secretBase32, long unixSeconds, int digits = 6, int period = 30, string algorithm = "SHA1")
    {
        byte[] key = Base32Decode(secretBase32);
        long counter = unixSeconds / period;

        byte[] ctr = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(ctr);   // 8-byte big-endian counter

        using HMAC hmac = algorithm.ToUpperInvariant() switch
        {
            "SHA256" => new HMACSHA256(key),
            "SHA512" => new HMACSHA512(key),
            _ => new HMACSHA1(key),
        };
        byte[] hash = hmac.ComputeHash(ctr);

        int offset = hash[^1] & 0x0F;
        int bin = ((hash[offset] & 0x7F) << 24)
                | ((hash[offset + 1] & 0xFF) << 16)
                | ((hash[offset + 2] & 0xFF) << 8)
                | (hash[offset + 3] & 0xFF);

        int otp = bin % (int)Math.Pow(10, digits);
        return otp.ToString().PadLeft(digits, '0');
    }

    public static int SecondsRemaining(long unixSeconds, int period = 30)
        => period - (int)(unixSeconds % period);

    // RFC 4648 Base32 decode (upper/lowercase, ignores spaces and padding).
    public static byte[] Base32Decode(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int bits = 0, value = 0;
        foreach (char raw in s)
        {
            char c = char.ToUpperInvariant(raw);
            if (c == '=' || c == ' ' || c == '-') continue;
            int idx = alphabet.IndexOf(c);
            if (idx < 0) continue;
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);
            dict[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return dict;
    }
}
