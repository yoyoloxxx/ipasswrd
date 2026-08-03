using System.Security.Cryptography;
using System.Text;

namespace IPasswrd.Core;

/// <summary>
/// Have I Been Pwned "Pwned Passwords" range lookup, done with k-anonymity so the
/// full password (or even its full hash) never leaves the machine:
///
///   sha1 = SHA-1(password) as uppercase hex (40 chars)
///   prefix = sha1[..5]   (sent to the API)
///   suffix = sha1[5..]   (matched locally against the range response)
///
/// The API returns every suffix that shares the 5-char prefix, one per line as
/// "SUFFIX:COUNT". We look up our suffix in that list; COUNT is how many times the
/// password appears in known breaches (0 = not found). Only the 5-char prefix is
/// ever transmitted, so the server cannot learn which password was checked.
///
/// This type contains NO networking: the caller supplies a fetch delegate
/// (prefix -> range body). That keeps the Core assembly transport-free and lets the
/// parsing / hashing be unit-tested without touching the network.
/// </summary>
public static class BreachCheck
{
    /// <summary>SHA-1 of the UTF-8 password as a 40-char uppercase hex string.</summary>
    public static string Sha1Hex(string password)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(password ?? ""));
        return Convert.ToHexString(hash);   // uppercase, no separators
    }

    /// <summary>First 5 hex chars of the SHA-1 — the only thing sent to the API.</summary>
    public static string Prefix(string password) => Sha1Hex(password)[..5];

    /// <summary>The remaining 35 hex chars — matched locally against the range body.</summary>
    public static string Suffix(string password) => Sha1Hex(password)[5..];

    /// <summary>
    /// Given a raw range-endpoint body (lines of "SUFFIX:COUNT", with or without the
    /// optional padding lines HIBP adds when Add-Padding is requested), return the breach
    /// count for <paramref name="password"/>. 0 means the password was not found.
    /// </summary>
    public static long CountInBody(string password, string rangeBody)
    {
        if (string.IsNullOrEmpty(rangeBody)) return 0;
        string suffix = Suffix(password);
        foreach (var raw in rangeBody.Split('\n'))
        {
            var line = raw.AsSpan().Trim();
            if (line.IsEmpty) continue;
            int colon = line.IndexOf(':');
            if (colon != 35) continue;                                  // suffix is always 35 hex chars
            if (!line[..colon].Equals(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            // padding lines carry a count of 0 — a real hit never does
            return long.TryParse(line[(colon + 1)..], out long n) ? n : 0;
        }
        return 0;
    }

    /// <summary>
    /// Check one password. <paramref name="fetchRange"/> receives the 5-char prefix and must
    /// return the range endpoint's text body (e.g. GET https://api.pwnedpasswords.com/range/{prefix}).
    /// Returns the breach count (0 = clean). Never throws for a "not found"; network/HTTP
    /// failures propagate out of <paramref name="fetchRange"/> for the caller to handle.
    /// </summary>
    public static async Task<long> CountAsync(string password, Func<string, Task<string>> fetchRange)
    {
        if (string.IsNullOrEmpty(password)) return 0;
        string body = await fetchRange(Prefix(password)).ConfigureAwait(false);
        return CountInBody(password, body);
    }
}
