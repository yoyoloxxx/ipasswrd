using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

public class DedupTests
{
    private static VaultItem Acc(string url, string user, string pass, string title = "") =>
        new() { Type = "account", Title = title, Fields = new() { ["url"] = url, ["username"] = user, ["password"] = pass } };

    [Theory]
    [InlineData("https://accounts.google.com/signin", "google.com")]
    [InlineData("https://www.google.com/", "google.com")]
    [InlineData("google.com", "google.com")]
    [InlineData("https://mail.yandex.ru", "yandex.ru")]
    [InlineData("https://foo.bar.example.co.uk/x", "example.co.uk")]
    [InlineData("http://10.90.90.2/24online/webpages/client.jsp", "10.90.90.2")]   // IP: never trimmed
    [InlineData("https://192.168.1.1/", "192.168.1.1")]
    [InlineData("https://victim.github.io/repo", "victim.github.io")]   // multi-tenant suffix: tenants stay separate
    [InlineData("https://a.victim.github.io", "victim.github.io")]
    [InlineData("https://myapp.web.app", "myapp.web.app")]
    [InlineData("https://site.herokuapp.com/x", "site.herokuapp.com")]
    [InlineData("https://foo.s3.amazonaws.com", "foo.s3.amazonaws.com")]
    [InlineData("https://github.io", "github.io")]   // bare public suffix: returned as-is, never collapsed
    public void RegistrableDomain_Collapses_Subdomains(string url, string expected)
        => Assert.Equal(expected, Dedup.RegistrableDomain(url));

    [Fact]   // security: two tenants on a shared public suffix must NOT be the same registrable domain
    public void SharedPublicSuffix_Tenants_AreDistinct()
    {
        Assert.NotEqual(Dedup.RegistrableDomain("https://victim.github.io"),
                        Dedup.RegistrableDomain("https://attacker.github.io"));
        Assert.False(Dedup.RegistrableDomain("https://attacker.github.io") == "github.io");
        Assert.True(Dedup.IsPublicSuffix("github.io"));
        Assert.False(Dedup.IsPublicSuffix("victim.github.io"));
    }

    [Fact]
    public void SameBase_SameLogin_SamePassword_AreDuplicates()
    {
        var a = Acc("https://google.com", "u@x.com", "p");
        var b = Acc("https://accounts.google.com", "u@x.com", "p");
        Assert.Equal(Dedup.Key(a), Dedup.Key(b));
    }

    [Fact]
    public void DifferentLogin_NotDuplicate()
    {
        var a = Acc("https://google.com", "a@x.com", "p");
        var b = Acc("https://accounts.google.com", "b@x.com", "p");
        Assert.NotEqual(Dedup.Key(a), Dedup.Key(b));
    }

    [Fact]   // safety: a password difference must never be merged away (could be a password change)
    public void DifferentPassword_NotDuplicate()
    {
        var a = Acc("https://google.com", "u@x.com", "p1");
        var b = Acc("https://accounts.google.com", "u@x.com", "p2");
        Assert.NotEqual(Dedup.Key(a), Dedup.Key(b));
    }

    [Fact]
    public void Collapse_Keeps_LowestLevelHost_And_ItsTitle()
    {
        var sub = Acc("https://accounts.google.com", "u@x.com", "p", "Accounts Google (1)");
        var root = Acc("https://google.com", "u@x.com", "p", "google.com");
        var outp = Dedup.Collapse(new[] { sub, root });   // subdomain listed first — survivor must still be the base domain
        Assert.Single(outp);
        Assert.Equal("google.com", outp[0].Fields["url"].Replace("https://", ""));
        Assert.Equal("google.com", outp[0].Title);
    }

    [Fact]
    public void Collapse_Keeps_Distinct_Logins_And_Passwords()
    {
        var items = new[]
        {
            Acc("https://google.com", "a@x.com", "p"),
            Acc("https://accounts.google.com", "a@x.com", "p"),   // duplicate of #1
            Acc("https://google.com", "b@x.com", "p"),            // different login -> kept
            Acc("https://google.com", "a@x.com", "OTHER"),        // different password -> kept
        };
        var outp = Dedup.Collapse(items);
        Assert.Equal(3, outp.Count);
    }

    [Fact]
    public void Notes_Only_Collapse_When_Identical()
    {
        var n1 = new VaultItem { Type = "note", Title = "T", Notes = "one" };
        var n2 = new VaultItem { Type = "note", Title = "T", Notes = "two" };
        Assert.NotEqual(Dedup.Key(n1), Dedup.Key(n2));
    }
}
