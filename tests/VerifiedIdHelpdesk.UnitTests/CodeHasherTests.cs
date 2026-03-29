using VerifiedIdHelpdesk.Infrastructure;

namespace VerifiedIdHelpdesk.UnitTests;

public class CodeHasherTests
{
    // 32 zero bytes encoded as base64 — valid HMAC key for tests
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
}
