using VerifiedIdHelpdesk.Core;
using VerifiedIdHelpdesk.Infrastructure;

namespace VerifiedIdHelpdesk.UnitTests;

/// <summary>
/// Unit tests for <see cref="CodeGenerator"/>.
///
/// Security significance: The code generator is a critical security component.
/// It must produce cryptographically random codes of the correct length, using
/// only characters from the approved charset. Codes must be unique in practice
/// (probabilistic guarantee). The charset must exclude visually confusable
/// characters (0/O, 1/I/L) to prevent user error when reading codes aloud.
/// </summary>
public class CodeGeneratorTests
{
    // ── Length and charset ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that each generated code has exactly <see cref="Constants.CodeLength"/> characters.
    /// A shorter code dramatically weakens brute-force resistance; a longer one is never expected
    /// by the UI or storage layer.
    /// </summary>
    [Fact]
    public void Generate_ReturnsCodeOfExactlyCodeLength()
    {
        var code = CodeGenerator.Generate();
        Assert.Equal(Constants.CodeLength, code.Length);
    }

    /// <summary>
    /// Verifies that every character in the generated code is a member of
    /// <see cref="Constants.CodeCharset"/>. Any character outside the charset
    /// would be invisible or unreadable on screen (e.g., control chars) or could
    /// indicate a bias in the generator.
    /// </summary>
    [Fact]
    public void Generate_AllCharactersAreFromCodeCharset()
    {
        // Run several iterations to reduce the chance of a lucky single-pass.
        for (int i = 0; i < 20; i++)
        {
            var code = CodeGenerator.Generate();
            Assert.All(code, ch => Assert.Contains(ch, Constants.CodeCharset));
        }
    }

    // ── Display formatting ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="CodeGenerator.FormatForDisplay"/> produces an
    /// "XXXX-XXXX" string: total length 9, hyphen at index 4, original 8 chars intact.
    /// The formatted code is what the agent reads to the caller, so the format must be exact.
    /// </summary>
    [Theory]
    [InlineData("ABCDEFGH", "ABCD-EFGH")]
    [InlineData("ZZZZZZZZ", "ZZZZ-ZZZZ")]
    [InlineData("23456789", "2345-6789")]
    [InlineData("A2B3C4D5", "A2B3-C4D5")]
    public void FormatForDisplay_InsertsHyphenAtPositionFour(string raw, string expected)
    {
        Assert.Equal(expected, CodeGenerator.FormatForDisplay(raw));
    }

    /// <summary>
    /// Verifies that the formatted code has the expected shape:
    /// total length == CodeLength + 1 (the hyphen), and the hyphen is at index 4.
    /// </summary>
    [Fact]
    public void FormatForDisplay_HasCorrectLengthAndHyphenPosition()
    {
        var code = CodeGenerator.Generate();
        var formatted = CodeGenerator.FormatForDisplay(code);

        Assert.Equal(Constants.CodeLength + 1, formatted.Length); // "XXXX-XXXX" = 9 chars
        Assert.Equal('-', formatted[4]);
    }

    // ── Uniqueness / randomness ───────────────────────────────────────────────

    /// <summary>
    /// Verifies that 100 successive calls produce no duplicates.
    /// The probability of a collision in a 30^8 ≈ 6.5 × 10^11 space over 100 draws is
    /// negligible (~7.5 × 10^-10), so a collision would almost certainly indicate a broken RNG.
    /// Relies on <see cref="System.Security.Cryptography.RandomNumberGenerator"/> — never
    /// System.Random or Guid.
    /// </summary>
    [Fact]
    public void Generate_ProducesUniqueCodesAcrossIterations()
    {
        const int iterations = 100;
        var codes = Enumerable.Range(0, iterations).Select(_ => CodeGenerator.Generate()).ToList();
        Assert.Equal(iterations, codes.Distinct().Count());
    }

    // ── Security: no confusable characters ────────────────────────────────────

    /// <summary>
    /// Verifies that the charset excludes characters that are visually confusable
    /// when read aloud over the phone: '0' vs 'O', '1' vs 'I' vs 'L'.
    /// If these were present, callers could transcribe the wrong character,
    /// causing unnecessary failed attempts and a degraded user experience.
    /// </summary>
    [Theory]
    [InlineData('0', "zero")]
    [InlineData('O', "letter O")]
    [InlineData('1', "one")]
    [InlineData('I', "letter I")]
    [InlineData('L', "letter L")]
    public void CodeCharset_DoesNotContainConfusableCharacters(char confusable, string _)
    {
        // xUnit's Assert.Contains/DoesNotContain on string treats it as IEnumerable<char>.
        Assert.DoesNotContain(confusable, Constants.CodeCharset);
    }
}
