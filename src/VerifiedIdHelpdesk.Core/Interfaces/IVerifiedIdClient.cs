using VerifiedIdHelpdesk.Core.Models;

namespace VerifiedIdHelpdesk.Core.Interfaces;

/// <summary>
/// Client for the Microsoft Entra Verified ID Request Service REST API.
/// Implemented by <c>EntraVerifiedIdClient</c> in the Infrastructure project.
/// </summary>
public interface IVerifiedIdClient
{
    /// <summary>
    /// Creates a presentation request — asks the caller to present their Verified ID credential
    /// via Microsoft Authenticator. Returns a QR code URL and deep link.
    /// </summary>
    /// <param name="sessionId">Session ID to include in the request state (correlates the callback).</param>
    /// <param name="callbackUrl">The webhook URL where the Verified ID service will POST the result.</param>
    /// <param name="callbackApiKey">One-time callback token the Verified ID service must echo in the api-key header.</param>
    Task<PresentationRequestResult> CreatePresentationRequestAsync(string sessionId, string callbackUrl, string callbackApiKey);
}
