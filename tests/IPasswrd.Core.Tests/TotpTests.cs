using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

public class TotpTests
{
    // RFC 6238 Appendix B test vectors (SHA1, seed "12345678901234567890", 8 digits).
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";   // base32 of the ASCII seed

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Matches_Rfc6238_Sha1_Vectors(long time, string expected)
    {
        Assert.Equal(expected, Totp.Generate(Secret, time, digits: 8, period: 30, algorithm: "SHA1"));
    }

    [Fact]
    public void Six_Digit_Is_Last_Six_Of_Eight()
    {
        Assert.Equal("287082", Totp.Generate(Secret, 59, digits: 6));
    }

    [Fact]
    public void Parses_Otpauth_Uri()
    {
        var c = Totp.Parse("otpauth://totp/GitHub:gleb?secret=" + Secret + "&issuer=GitHub&algorithm=SHA1&digits=6&period=30");
        Assert.Equal(Secret, c.Secret);
        Assert.Equal(6, c.Digits);
        Assert.Equal(30, c.Period);
        Assert.Equal("SHA1", c.Algorithm);
        Assert.Equal("287082", Totp.Generate(c.Secret, 59, c.Digits, c.Period, c.Algorithm));
    }

    [Fact]
    public void Raw_Secret_With_Spaces_Is_Accepted()
    {
        var c = Totp.Parse("GEZD GNBV GY3T QOJQ GEZD GNBV GY3T QOJQ");
        Assert.Equal("94287082", Totp.Generate(c.Secret, 59, 8));
    }

    [Fact]
    public void Seconds_Remaining_In_Range()
    {
        int s = Totp.SecondsRemaining(45, 30);   // 45 % 30 = 15 -> 15 left
        Assert.Equal(15, s);
    }

    [Fact]
    public void Parse_Extracts_Issuer_And_Account_From_Uri()
    {
        var c = Totp.Parse("otpauth://totp/GitHub:gleb@hse.ru?secret=" + Secret + "&issuer=GitHub");
        Assert.Equal("GitHub", c.Issuer);
        Assert.Equal("gleb@hse.ru", c.Account);
    }

    [Fact]
    public void Parse_Issuer_From_Label_When_No_Query_Param()
    {
        var c = Totp.Parse("otpauth://totp/Google:gleb@gmail.com?secret=" + Secret);
        Assert.Equal("Google", c.Issuer);          // "Issuer:Account" label convention
        Assert.Equal("gleb@gmail.com", c.Account);
    }

    [Fact]
    public void Raw_Secret_Has_No_Issuer_Or_Account()
    {
        var c = Totp.Parse(Secret);
        Assert.Equal("", c.Issuer);
        Assert.Equal("", c.Account);
    }

    [Theory]
    [InlineData("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", true)]                        // raw base32
    [InlineData("GEZD GNBV GY3T QOJQ", true)]                                     // spaced base32
    [InlineData("otpauth://totp/X?secret=GEZDGNBVGY3TQOJQ&issuer=X", true)]       // otpauth uri
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("otpauth://totp/X?issuer=X", false)]                              // uri without a secret
    [InlineData("!!!!", false)]                                                   // no base32 chars -> empty key
    public void IsValidSecret_Accepts_Real_Secrets_Only(string input, bool ok)
    {
        Assert.Equal(ok, Totp.IsValidSecret(input));
    }
}
