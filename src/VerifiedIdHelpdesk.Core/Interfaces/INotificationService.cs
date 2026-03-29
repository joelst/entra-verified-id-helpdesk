namespace VerifiedIdHelpdesk.Core.Interfaces;

/// <summary>
/// Sends the one-time verification code to the caller via their chosen channel.
/// Implemented in the Notifications project.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Sends the verification code to the recipient.
    /// </summary>
    /// <param name="recipientEmail">Caller's email address (used for email and to look up Teams user).</param>
    /// <param name="displayCode">The formatted code (e.g., "X7K2-PQ9R") to include in the message.</param>
    /// <param name="expiresAt">Code expiry time shown in the message body.</param>
    /// <param name="channel">Delivery channel: "email", "teams", or "sms".</param>
    Task SendCodeAsync(string recipientEmail, string displayCode, DateTime expiresAt, string channel);
}
