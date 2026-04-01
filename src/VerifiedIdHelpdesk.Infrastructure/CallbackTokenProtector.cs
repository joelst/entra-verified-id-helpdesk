using System.Security.Cryptography;
using System.Text;

namespace VerifiedIdHelpdesk.Infrastructure;

public static class CallbackTokenProtector
{
  public static string Generate()
  {
    return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
  }

  public static string Hash(string token)
  {
    var data = Encoding.UTF8.GetBytes(token);
    return Convert.ToHexString(SHA256.HashData(data));
  }

  public static bool Matches(string token, string expectedHash)
  {
    if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(expectedHash))
      return false;

    var providedHash = Hash(token);
    var expectedBytes = Encoding.UTF8.GetBytes(expectedHash);
    var providedBytes = Encoding.UTF8.GetBytes(providedHash);
    return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
  }
}