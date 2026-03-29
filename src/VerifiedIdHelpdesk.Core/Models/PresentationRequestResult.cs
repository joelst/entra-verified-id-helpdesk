namespace VerifiedIdHelpdesk.Core.Models;

/// <summary>
/// Result returned by the Entra Verified ID Request Service after creating a presentation request.
/// </summary>
public class PresentationRequestResult
{
    /// <summary>
    /// The Entra Verified ID request ID. Stored in the session and used to correlate webhook callbacks.
    /// </summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>
    /// URL of the QR code image (data URI or hosted image URL) to display to the caller.
    /// The caller scans this with Microsoft Authenticator to initiate verification.
    /// </summary>
    public string QrCodeUri { get; set; } = string.Empty;

    /// <summary>
    /// Deep link URL (openid-vc://...) for mobile users to open directly in Microsoft Authenticator.
    /// </summary>
    public string DeepLink { get; set; } = string.Empty;

    /// <summary>
    /// URL for mobile users to open Microsoft Authenticator directly.
    /// </summary>
    public string? MsAuthenticatorDeepLink { get; set; }
}
