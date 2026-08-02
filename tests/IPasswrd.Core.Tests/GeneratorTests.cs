using IPasswrd.Core;
using Xunit;

namespace IPasswrd.Core.Tests;

public class GeneratorTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(64)]
    public void Respects_Length(int len)
    {
        Assert.Equal(len, Generator.Generate(len).Length);
    }

    [Fact]
    public void Uses_Only_Chars_From_Pool()
    {
        var o = new GeneratorOptions(Length: 200);
        var pool = Generator.Pool(o).ToHashSet();
        Assert.All(Generator.Generate(o), c => Assert.Contains(c, pool));
    }

    [Fact]
    public void Excludes_Ambiguous_Characters()
    {
        var pool = Generator.Pool(new GeneratorOptions(ExcludeAmbiguous: true));
        foreach (char c in "lIO01")
            Assert.DoesNotContain(c, pool);
    }

    [Fact]
    public void Digits_Only_Yields_Digits()
    {
        var o = new GeneratorOptions(Length: 40, Lower: false, Upper: false, Digits: true, Symbols: false);
        Assert.All(Generator.Generate(o), c => Assert.True(char.IsDigit(c)));
    }

    [Fact]
    public void Two_Generations_Differ()
    {
        Assert.NotEqual(Generator.Generate(24), Generator.Generate(24));
    }

    [Fact]
    public void No_Classes_Selected_Yields_Empty()
    {
        var o = new GeneratorOptions(Lower: false, Upper: false, Digits: false, Symbols: false);
        Assert.Equal("", Generator.Generate(o));
    }
}
