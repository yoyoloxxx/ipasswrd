using System.Security.Cryptography;
using System.Text;
using IPasswrd.Mobile.Services;
using Xunit;

namespace IPasswrd.Core.Tests;

/// <summary>Audit M2: AppUpdate must refuse unexpected hosts and SHA-256 mismatches.</summary>
public class AppUpdateIntegrityTests
{
    private const string OfficialApk =
        "https://github.com/yoyoloxxx/ipasswrd/releases/download/mobile-latest/IPasswrd.apk";

    private static string Hex64(char c) => new string(c, 64);

    [Theory]
    [InlineData(OfficialApk)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/123/abc")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/xyz")]
    public void Allowed_Official_Hosts(string url)
        => Assert.True(AppUpdateIntegrity.IsAllowedDownloadUrl(url));

    [Theory]
    [InlineData("http://github.com/yoyoloxxx/ipasswrd/releases/download/mobile-latest/IPasswrd.apk")] // not https
    [InlineData("https://evil.com/IPasswrd.apk")]
    [InlineData("https://github.com/evil/ipasswrd/releases/download/mobile-latest/IPasswrd.apk")]
    [InlineData("https://github.com/yoyoloxxx/ipasswrd/archive/refs/heads/main.zip")] // not /releases/download/
    [InlineData("https://raw.githubusercontent.com/yoyoloxxx/ipasswrd/main/IPasswrd.apk")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_Unexpected_Hosts_Or_Paths(string? url)
        => Assert.False(AppUpdateIntegrity.IsAllowedDownloadUrl(url));

    [Fact]
    public void Sha256_Must_Be_64_Hex_Digits()
    {
        Assert.True(AppUpdateIntegrity.IsValidSha256Hex(Hex64('a')));
        Assert.True(AppUpdateIntegrity.IsValidSha256Hex(Hex64('A')));
        Assert.False(AppUpdateIntegrity.IsValidSha256Hex(Hex64('g')));
        Assert.False(AppUpdateIntegrity.IsValidSha256Hex(new string('a', 63)));
        Assert.False(AppUpdateIntegrity.IsValidSha256Hex(new string('a', 65)));
        Assert.False(AppUpdateIntegrity.IsValidSha256Hex(null));
        Assert.False(AppUpdateIntegrity.IsValidSha256Hex(""));
    }

    [Fact]
    public void Hash_Mismatch_Refuses_Install()
    {
        string expected = AppUpdateIntegrity.Sha256Hex("good-apk-bytes");
        string actual = AppUpdateIntegrity.Sha256Hex("tampered-apk-bytes");

        Assert.False(AppUpdateIntegrity.AcceptDownloadedApk(OfficialApk, expected, actual));
    }

    [Fact]
    public void Hash_Match_On_Allowed_Url_Accepts()
    {
        string digest = AppUpdateIntegrity.Sha256Hex("good-apk-bytes");
        Assert.True(AppUpdateIntegrity.AcceptDownloadedApk(OfficialApk, digest, digest));
        Assert.True(AppUpdateIntegrity.AcceptDownloadedApk(OfficialApk, digest.ToUpperInvariant(), digest));
    }

    [Fact]
    public void Hash_Match_On_Disallowed_Url_Still_Refuses()
    {
        string digest = AppUpdateIntegrity.Sha256Hex("good-apk-bytes");
        Assert.False(AppUpdateIntegrity.AcceptDownloadedApk(
            "https://evil.example/IPasswrd.apk", digest, digest));
    }

    [Fact]
    public async Task File_Hash_Mismatch_Refuses()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ipw-update-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "payload.bin");
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes("release-apk-body");
            await File.WriteAllBytesAsync(path, payload);

            string expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            string wrong = AppUpdateIntegrity.Sha256Hex("other");
            string actual = await AppUpdateIntegrity.Sha256FileHexAsync(path);

            Assert.Equal(expected, actual);
            Assert.False(AppUpdateIntegrity.AcceptDownloadedApk(OfficialApk, wrong, actual));
            Assert.True(AppUpdateIntegrity.AcceptDownloadedApk(OfficialApk, expected, actual));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Android_Build_Workflow_Emits_Sha256_Field()
    {
        string? dir = AppContext.BaseDirectory;
        string? root = null;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "IPasswrd.sln"))) { root = dir; break; }
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.NotNull(root);
        string yml = File.ReadAllText(Path.Combine(root!, ".github", "workflows", "android-build.yml"));
        Assert.Contains("sha256sum out/IPasswrd.apk", yml);
        Assert.Contains("\"sha256\":\"%s\"", yml);
    }
}
