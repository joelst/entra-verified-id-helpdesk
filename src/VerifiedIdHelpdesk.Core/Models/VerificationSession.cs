namespace VerifiedIdHelpdesk.Core.Models;

/// <summary>
/// Represents a single caller identity verification session.
/// One session is created per helpdesk call and expires after <see cref="Constants.CodeExpiryMinutes"/> minutes.
/// </summary>
public class VerificationSession
{
    /// <summary>
    /// Unique session identifier (GUID). Used as the Table Storage RowKey.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// HMAC-SHA256 hash of the one-time code.
    /// SECURITY: The plaintext code is NEVER stored — only this hash.
    /// </summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>
    /// The caller's email address. Used to match the code submission.
    /// </summary>
    public string CallerEmail { get; set; } = string.Empty;

    /// <summary>
    /// The caller's Entra ID object ID, selected by the agent from the directory.
    /// </summary>
    public string CallerEntraId { get; set; } = string.Empty;

    /// <summary>
    /// The caller's display name (from Entra directory, selected by agent).
    /// </summary>
    public string CallerDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Optional helpdesk ticket ID. Free-text, not validated. Stored for audit trail.
    /// </summary>
    public string TicketId { get; set; } = string.Empty;

    /// <summary>
    /// Optional agent note (max 500 characters). Stored for audit trail.
    /// </summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>
    /// Entra ID object ID of the helpdesk agent who initiated this session.
    /// </summary>
    public string AgentEntraId { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the helpdesk agent who initiated this session.
    /// </summary>
    public string AgentDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Notification channel used to deliver the code: "email", "teams", or "sms".
    /// </summary>
    public string DeliveryChannel { get; set; } = string.Empty;

    /// <summary>
    /// Current session status: "pending", "verified", "expired", or "failed".
    /// </summary>
    public string Status { get; set; } = SessionStatus.Pending;

    /// <summary>
    /// Number of failed code entry attempts. Locked after <see cref="Constants.MaxFailedAttempts"/>.
    /// SECURITY: Prevents brute-force code guessing.
    /// </summary>
    public int FailedAttempts { get; set; }

    /// <summary>
    /// JSON string of claims returned by Entra Verified ID after successful presentation.
    /// Contains displayName, employeeId, department. Only populated when Status == "verified".
    /// </summary>
    public string? VerifiedClaims { get; set; }

    /// <summary>
    /// Entra Verified ID request ID returned by the presentation request. Used to correlate callbacks.
    /// </summary>
    public string? RequestId { get; set; }

    /// <summary>
    /// SHA-256 hash of the one-time callback token sent to the Verified ID service.
    /// SECURITY: The raw callback token is never persisted.
    /// </summary>
    public string? CallbackTokenHash { get; set; }

    /// <summary>
    /// UTC timestamp when this session was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// UTC timestamp when this session expires (CreatedAt + <see cref="Constants.CodeExpiryMinutes"/> minutes).
    /// SECURITY: Always validated server-side. Never trust client-supplied expiry values.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// UTC timestamp when the caller successfully verified their identity. Null until verified.
    /// </summary>
    public DateTime? VerifiedAt { get; set; }
}
