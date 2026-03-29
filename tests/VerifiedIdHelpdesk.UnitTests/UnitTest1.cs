using VerifiedIdHelpdesk.Core;
using VerifiedIdHelpdesk.Infrastructure;

namespace VerifiedIdHelpdesk.UnitTests;

public class CodeGeneratorTests
{
    [Fact]
    public void Generate_ReturnsEightCharacterCode()
    {
        var code = CodeGenerator.Generate();
        Assert.Equal(Constants.CodeLength, code.Length);
    }

    [Fact]
    public void Generate_OnlyUsesValidCharset()
    {
        var code = CodeGenerator.Generate();
        Assert.All(code, c => Assert.Contains(c, Constants.CodeCharset));
    }

    [Fact]
    public void Generate_ProducesDifferentCodes()
    {
        // Statistically, 20 random codes from a 36^8 space should all be unique
        var codes = Enumerable.Range(0, 20).Select(_ => CodeGenerator.Generate()).ToList();
        Assert.Equal(20, codes.Distinct().Count());
    }

    [Theory]
    [InlineData("ABCD1234", "ABCD-1234")]
    [InlineData("ZZZZZZZZ", "ZZZZ-ZZZZ")]
    [InlineData("12345678", "1234-5678")]
    public void FormatForDisplay_InsertsHyphenAtPosition4(string code, string expected)
    {
        Assert.Equal(expected, CodeGenerator.FormatForDisplay(code));
    }
}
