using System.Threading.Tasks;
using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

public class BreachCheckTests
{
    // Well-known vector: SHA-1("password") = 5BAA61E4C9B93F3F0682250B6CF8331B7EE68FD8
    [Fact]
    public void Sha1_Prefix_Suffix_Match_Known_Vector()
    {
        Assert.Equal("5BAA61E4C9B93F3F0682250B6CF8331B7EE68FD8", BreachCheck.Sha1Hex("password"));
        Assert.Equal("5BAA6", BreachCheck.Prefix("password"));
        Assert.Equal("1E4C9B93F3F0682250B6CF8331B7EE68FD8", BreachCheck.Suffix("password"));
    }

    [Fact]
    public void Prefix_Is_Always_Five_Hex_Chars()
    {
        foreach (var pw in new[] { "a", "hunter2", "correct horse battery staple", "Пароль", "" })
        {
            string p = BreachCheck.Prefix(pw);
            Assert.Equal(5, p.Length);
            Assert.All(p, c => Assert.Contains(c, "0123456789ABCDEF"));
        }
    }

    [Fact]
    public void CountInBody_Finds_Suffix_Case_Insensitive()
    {
        // range body as returned for prefix 5BAA6 (suffix of "password" + two decoys)
        string body =
            "0018A45C4D1DEF81644B54AB7F969B88D65:1\r\n" +
            "1E4C9B93F3F0682250B6CF8331B7EE68FD8:37359195\r\n" +   // "password"
            "011053FD0102E94D6AE2F8B83D76FAF94F6:3";
        Assert.Equal(37359195, BreachCheck.CountInBody("password", body));
    }

    [Fact]
    public void CountInBody_Lowercase_Suffix_Line_Still_Matches()
    {
        string body = "1e4c9b93f3f0682250b6cf8331b7ee68fd8:5";
        Assert.Equal(5, BreachCheck.CountInBody("password", body));
    }

    [Fact]
    public void CountInBody_Returns_Zero_When_Absent()
    {
        string body = "0018A45C4D1DEF81644B54AB7F969B88D65:1\r\n011053FD0102E94D6AE2F8B83D76FAF94F6:3";
        Assert.Equal(0, BreachCheck.CountInBody("password", body));
    }

    [Fact]
    public void CountInBody_Ignores_Zero_Count_Padding_Line()
    {
        // HIBP padding lines carry the real-looking suffix shape but a count of 0
        string body = "1E4C9B93F3F0682250B6CF8331B7EE68FD8:0";
        Assert.Equal(0, BreachCheck.CountInBody("password", body));
    }

    [Fact]
    public void CountInBody_Empty_Body_Is_Zero()
    {
        Assert.Equal(0, BreachCheck.CountInBody("password", ""));
    }

    [Fact]
    public async Task CountAsync_Sends_Only_Prefix_And_Parses_Body()
    {
        string? seenPrefix = null;
        long n = await BreachCheck.CountAsync("password", prefix =>
        {
            seenPrefix = prefix;
            // the fake endpoint only ever receives the 5-char prefix
            return Task.FromResult("1E4C9B93F3F0682250B6CF8331B7EE68FD8:9\n");
        });
        Assert.Equal("5BAA6", seenPrefix);
        Assert.Equal(9, n);
    }

    [Fact]
    public async Task CountAsync_Empty_Password_Skips_Fetch()
    {
        bool fetched = false;
        long n = await BreachCheck.CountAsync("", _ => { fetched = true; return Task.FromResult(""); });
        Assert.Equal(0, n);
        Assert.False(fetched);
    }
}
