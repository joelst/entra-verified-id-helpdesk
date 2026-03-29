using System.Security.Cryptography;
using System.Text;

namespace VerifiedIdHelpdesk.Infrastructure;

public static class CodeHasher
{
    /// <summary>
    /// Computes HMAC-SHA256 of the normalized (uppercased) code.
    /// NEVER store or log the plaintext code — only this hash.
    /// </summary>
    public static string Hash(string code, string hmacKeyBase64)
    {
        var key = Convert.FromBase64String(hmacKeyBase64);
        var data = Encoding.UTF8.GetBytes(code.ToUpperInvariant());
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(data));
    }
}
