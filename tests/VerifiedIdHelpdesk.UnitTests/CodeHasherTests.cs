using VerifiedIdHelpdesk.Infrastructure;

namespace VerifiedIdHelpdesk.UnitTests;

/// <summary>
/// Unit tests for <see cref="CodeHasher"/>.
///
/// Security significance: The HMAC hash is the ONLY representation of the one-time
/// code that is ever persisted. Plaintext codes must never be stored. These tests
/// verify determinism, key-sensitivity, case-insensitivity, and that the output is
/// the correct hash (not the plaintext). A regression here could expose plaintext
/// codes in storage or break the code-lookup flow.
/// </summary>
public class CodeHasherTests
{
    // 32 zero bytes encoded as base64 — valid fixed HMAC key for deterministic tests.
    private const string TestKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    [Fact]
    public void Hash_ReturnsDeterministicResult()
    {
        var h1 = CodeHasher.Hash("ABCD1234", TestKey);
        var h2 = CodeHasher.Hash("ABCD1234", TestKey);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void Hash_IsCaseInsensitive_LowercaseEqualsUppercase()
    {
        var lower = CodeHasher.Hash("abcd1234", TestKey);
        var upper = CodeHasher.Hash("ABCD1234", TestKey);
        Assert.Equal(lower, upper);
    }

    [Fact]
    public void Hash_DifferentCodesProduceDifferentHashes()
    {
        var h1 = CodeHasher.Hash("ABCD1234", TestKey);
        var h2 = CodeHasher.Hash("ABCD1235", TestKey);
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void Hash_ReturnsValidBase64Of32Bytes()
    {
        var hash = CodeHasher.Hash("ABCD1234", TestKey);
        var bytes = Convert.FromBase64String(hash);
        Assert.Equal(32, bytes.Length); // HMAC-SHA256 output is 32 bytes
    }

    [Fact]
    public void Hash_DifferentKeysProduceDifferentHashes()
    {
        var key2 = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
        var h1 = CodeHasher.Hash("ABCD1234", TestKey);
        var h2 = CodeHasher.Hash("ABCD1234", key2);
        Assert.NotEqual(h1, h2);
    }

    /// <summary>
    /// Verifies that an invalid base64 key throws <see cref="FormatException"/>.
    /// The HMAC key comes from Key Vault as a base64 string; if the secret is
    /// misconfigured (e.g., stored as raw text instead of base64), the application
    /// must fail fast rather than silently producing incorrect hashes.
    /// </summary>
    [Fact]
    public void Hash_InvalidBase64Key_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => CodeHasher.Hash("ABCD1234", "not-valid-base64!!!"));
    }

    /// <summary>
    /// Verifies the hash output is not the plaintext code.
    /// This explicit assertion documents the security requirement: persisting the
    /// hash must never be confused with persisting the code.
    /// </summary>
    [Fact]
    public void Hash_OutputIsNotEqualToPlaintextCode()
    {
        const string code = "ABCD1234";
        var hash = CodeHasher.Hash(code, TestKey);
        Assert.NotEqual(code, hash);
    }
}
