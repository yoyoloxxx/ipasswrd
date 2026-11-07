using IPasswrd.App;
using Xunit;

namespace IPasswrd.Core.Tests;

// Audit L6–L8 live in the Windows app. Pure helpers are linked into this project so Linux CI
// can pin the security-relevant behaviour without a Windows TFM.
public class AuditLowWindowsFixesTests
{
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "IPasswrd.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        // Fallback: tests/IPasswrd.Core.Tests/bin/…/ → repo root is four levels up from bin.
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private static string ReadRepo(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    // ---- L7: helper file must not embed the live bearer ----

    [Fact]
    public void SmsRelayInfo_DoesNotEmbedCallerSuppliedToken()
    {
        const string live = "deadbeefcafebabe01234567";
        string text = SmsRelayInfoFile.Build("DESKTOP", 17346, new[] { "192.168.1.10" });

        Assert.DoesNotContain(live, text);
        Assert.Contains(SmsRelayInfoFile.TokenPlaceholder, text);
        Assert.Contains("sms-relay.token", text);
        Assert.Contains("X-IPW-Token", text);
        Assert.Contains("http://desktop.local:17346/sms?t=" + SmsRelayInfoFile.TokenPlaceholder, text);
        Assert.Contains("http://192.168.1.10:17346/sms?t=" + SmsRelayInfoFile.TokenPlaceholder, text);
    }

    [Fact]
    public void SmsRelay_Source_NoLongerInterpolatesLiveTokenIntoHelper()
    {
        string cs = ReadRepo(Path.Combine("windows", "IPasswrd.App", "SmsRelay.cs"));
        Assert.DoesNotContain("sms?t={_smsToken}", cs);
        Assert.DoesNotContain("clip?t={_smsToken}", cs);
        Assert.Contains("SmsRelayInfoFile.Build", cs);
    }

    // ---- L8: no absolute developer host path ----

    [Fact]
    public void NativeHostLocator_OnlyRelativeCandidates()
    {
        string[] paths = NativeHostLocator.SearchPaths(Path.Combine("C:", "Apps", "IPasswrd"));
        Assert.All(paths, p =>
        {
            Assert.DoesNotContain(@"D:\MyProjects", p, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MyProjects", p, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(paths, p => p.EndsWith("IPasswrd.Host.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NativeHostLocator_Resolve_PicksFirstExisting()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "ipw-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            string sibling = Path.GetFullPath(Path.Combine(baseDir, "..", "dist-host"));
            Directory.CreateDirectory(sibling);
            string host = Path.Combine(sibling, "IPasswrd.Host.exe");
            File.WriteAllText(host, "stub");

            string? found = NativeHostLocator.Resolve(baseDir);
            Assert.Equal(host, found);
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
            try { Directory.Delete(Path.GetFullPath(Path.Combine(baseDir, "..", "dist-host")), recursive: true); } catch { }
        }
    }

    [Fact]
    public void Features_Source_HasNoHardcodedDevHostPath()
    {
        string cs = ReadRepo(Path.Combine("windows", "IPasswrd.App", "Features.cs"));
        Assert.DoesNotContain(@"D:\MyProjects\IPasswrd", cs);
        Assert.Contains("NativeHostLocator.Resolve", cs);
    }

    // ---- L6: revoke path exists in Settings / bridge ----

    [Fact]
    public void BrowserBridge_ExposesRevokeExtensionPair()
    {
        string bridge = ReadRepo(Path.Combine("windows", "IPasswrd.App", "BrowserBridge.cs"));
        Assert.Contains("RevokeExtensionPair", bridge);
        Assert.Contains("IsExtensionPaired", bridge);

        string features = ReadRepo(Path.Combine("windows", "IPasswrd.App", "Features.cs"));
        Assert.Contains("RevokeExtensionPairRow", features);
        Assert.Contains("Отозвать", features);
    }
}
