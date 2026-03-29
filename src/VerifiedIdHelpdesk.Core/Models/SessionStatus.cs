namespace VerifiedIdHelpdesk.Core.Models;

/// <summary>
/// Well-known session status values. Use these constants instead of raw strings.
/// </summary>
public static class SessionStatus
{
    /// <summary>Code generated and sent; awaiting caller verification.</summary>
    public const string Pending = "pending";

    /// <summary>Caller successfully verified via Microsoft Authenticator.</summary>
    public const string Verified = "verified";

    /// <summary>Session exceeded the expiry time without verification.</summary>
    public const string Expired = "expired";

    /// <summary>Max failed attempts reached; session locked.</summary>
    public const string Failed = "failed";
}
