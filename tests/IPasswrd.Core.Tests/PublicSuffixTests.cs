using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

/// <summary>
/// Locks in the fix for the cross-tenant credential-leak the security audit found:
/// the registrable-domain boundary must use the full Public Suffix List, so sibling
/// tenants of a multi-tenant registry (evil.duckdns.org vs alice.duckdns.org) never
/// collapse to one "site" and never share a dedup / autofill key.
/// </summary>
public class PublicSuffixTests
{
    [Theory]
    // Previously-MISSING multi-tenant suffixes — sibling tenants MUST stay distinct.
    [InlineData("https://evil.duckdns.org", "evil.duckdns.org")]
    [InlineData("https://alice.duckdns.org", "alice.duckdns.org")]
    [InlineData("https://a.ngrok.io", "a.ngrok.io")]
    [InlineData("https://b.hopto.org", "b.hopto.org")]
    [InlineData("https://c.no-ip.org", "c.no-ip.org")]
    [InlineData("https://d.trycloudflare.com", "d.trycloudflare.com")]
    [InlineData("https://shop.myshopify.com", "shop.myshopify.com")]
    // Ordinary domains still collapse to eTLD+1.
    [InlineData("https://accounts.google.com", "google.com")]
    [InlineData("https://www.bank.co.uk", "bank.co.uk")]
    [InlineData("https://a.b.c.example.com", "example.com")]
    // Already-listed private suffix — regression guard.
    [InlineData("https://victim.github.io", "victim.github.io")]
    // Wildcard rule straight from the PSL (no leading "www" so Dedup's www-strip doesn't apply).
    [InlineData("https://foo.bar.ck", "foo.bar.ck")]   // *.ck  -> bar.ck is a public suffix
    public void RegistrableDomain_matches_PSL(string url, string expected)
        => Assert.Equal(expected, Dedup.RegistrableDomain(url));

    [Theory]
    // Exercised on PublicSuffix directly: Dedup.Host() strips a leading "www." as a dedup
    // convenience, which would mask the !www.ck exception rule. The PSL boundary itself is here.
    [InlineData("foo.bar.ck", "foo.bar.ck")]           // *.ck wildcard  -> bar.ck is a suffix
    [InlineData("www.ck", "www.ck")]                   // !www.ck exception -> registrable
    [InlineData("evil.duckdns.org", "evil.duckdns.org")]
    [InlineData("accounts.google.com", "google.com")]
    public void PublicSuffix_direct_rules(string host, string expected)
        => Assert.Equal(expected, PublicSuffix.RegistrableDomain(host));

    [Fact]
    public void SiblingTenants_of_multitenant_suffix_do_not_collapse()
    {
        // The exact leak: two tenants of a multi-tenant registry must never share a key.
        Assert.NotEqual(Dedup.RegistrableDomain("https://evil.duckdns.org"),
                        Dedup.RegistrableDomain("https://alice.duckdns.org"));
        Assert.NotEqual(Dedup.RegistrableDomain("https://a.ngrok.io"),
                        Dedup.RegistrableDomain("https://b.ngrok.io"));
    }

    [Theory]
    [InlineData("duckdns.org", true)]
    [InlineData("github.io", true)]
    [InlineData("com", true)]
    [InlineData("co.uk", true)]
    [InlineData("google.com", false)]
    [InlineData("evil.duckdns.org", false)]
    public void IsPublicSuffix_classifies(string domain, bool expected)
        => Assert.Equal(expected, PublicSuffix.IsPublicSuffix(domain));
}
