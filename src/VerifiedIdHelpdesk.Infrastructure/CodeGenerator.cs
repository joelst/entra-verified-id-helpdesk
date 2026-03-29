using System.Security.Cryptography;
using VerifiedIdHelpdesk.Core;

namespace VerifiedIdHelpdesk.Infrastructure;

public static class CodeGenerator
{
    public static string Generate()
    {
        var charset = Constants.CodeCharset;
        var bytes = RandomNumberGenerator.GetBytes(Constants.CodeLength * 2);
        var result = new char[Constants.CodeLength];
        for (int i = 0; i < Constants.CodeLength; i++)
            result[i] = charset[bytes[i] % charset.Length];
        return new string(result);
    }

    public static string FormatForDisplay(string code) =>
        code[..4] + "-" + code[4..];
}
