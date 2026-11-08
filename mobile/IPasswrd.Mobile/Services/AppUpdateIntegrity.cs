using System.Security.Cryptography;
using System.Text;

namespace IPasswrd.Mobile.Services;

/// <summary>
/// Pure update-integrity gates for Android OTA (audit M2). No MAUI / Android types so the
/// same helpers can be unit-tested from Core.Tests on Linux CI.
/// </summary>
public static class AppUpdateIntegrity
{
    public const string AllowedRepoPath = "/yoyoloxxx/ipasswrd/";

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    /// <summary>
    /// HTTPS + allow-listed GitHub release hosts. github.com paths must be this repo's
    /// <c>/releases/download/</c> tree; CDN hosts are opaque but still TLS-pinned to GitHub.
    /// </summary>
    public static bool IsAllowedDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u)) return false;
        if (!string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!AllowedHosts.Contains(u.Host)) return false;
        if (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            if (u.AbsolutePath.IndexOf(AllowedRepoPath, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (u.AbsolutePath.IndexOf("/releases/download/", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }
        return true;
    }

    /// <summary>True when <paramref name="hex"/> is exactly 64 lowercase/uppercase hex digits.</summary>
    public static bool IsValidSha256Hex(string? hex)
    {
        if (hex is null || hex.Length != 64) return false;
        for (int i = 0; i < hex.Length; i++)
        {
            char c = hex[i];
            bool ok = (c >= '0' && c <= '9')
                   || (c >= 'a' && c <= 'f')
                   || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    public static string NormalizeSha256Hex(string hex) => hex.ToLowerInvariant();

    public static string Sha256Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string utf8)
        => Sha256Hex(Encoding.UTF8.GetBytes(utf8));

    public static async Task<string> Sha256FileHexAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool Sha256Equals(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gate before Install: allow-listed URL, well-formed expected hash, and matching digest.
    /// A mismatch MUST refuse install (caller deletes the file).
    /// </summary>
    public static bool AcceptDownloadedApk(string? url, string? expectedSha256, string? actualSha256)
    {
        if (!IsAllowedDownloadUrl(url)) return false;
        if (!IsValidSha256Hex(expectedSha256) || !IsValidSha256Hex(actualSha256)) return false;
        return Sha256Equals(actualSha256!, expectedSha256!);
    }
}
