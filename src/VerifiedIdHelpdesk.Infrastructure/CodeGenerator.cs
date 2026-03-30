using System.Security.Cryptography;
using VerifiedIdHelpdesk.Core;

namespace VerifiedIdHelpdesk.Infrastructure;

public static class CodeGenerator
{
    public static string Generate()
    {
        return RandomNumberGenerator.GetString(Constants.CodeCharset, Constants.CodeLength);
    }

    public static string FormatForDisplay(string code) =>
        code[..4] + "-" + code[4..];
}
