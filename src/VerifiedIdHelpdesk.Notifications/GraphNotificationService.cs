using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using VerifiedIdHelpdesk.Core.Interfaces;

namespace VerifiedIdHelpdesk.Notifications;

public class GraphNotificationService : INotificationService
{
    private readonly GraphServiceClient _graph;
    private readonly IConfiguration _config;
    private readonly ILogger<GraphNotificationService> _logger;

    public GraphNotificationService(
        GraphServiceClient graph,
        IConfiguration config,
        ILogger<GraphNotificationService> logger)
    {
        _graph = graph;
        _config = config;
        _logger = logger;
    }

    public async Task SendCodeAsync(string recipientEmail, string displayCode, DateTime expiresAt, string channel)
    {
        switch (channel.ToLowerInvariant())
        {
            case "email":
                await SendEmailAsync(recipientEmail, displayCode, expiresAt);
                break;
            case "teams":
                await SendTeamsMessageAsync(recipientEmail, displayCode, expiresAt);
                break;
            default:
                _logger.LogWarning("Unsupported delivery channel: {Channel}", channel);
                throw new NotSupportedException($"Delivery channel '{channel}' is not supported.");
        }
    }

    private async Task SendEmailAsync(string recipientEmail, string displayCode, DateTime expiresAt)
    {
        var senderEmail = _config["Notifications:SenderEmail"]
            ?? throw new InvalidOperationException("Notifications:SenderEmail is not configured.");

        var verifyPortalUrl = _config["VerifyPortal:BaseUrl"]
            ?? throw new InvalidOperationException("VerifyPortal:BaseUrl is not configured.");

        var expiryText = expiresAt.ToString("HH:mm UTC");
        var body = $@"
<p>Your helpdesk agent has requested you verify your identity.</p>
<p><strong>Your one-time verification code is:</strong></p>
<h2 style='font-family:Courier New,monospace;letter-spacing:4px;'>{displayCode}</h2>
<p>This code expires at <strong>{expiryText}</strong>.</p>
<p>To verify your identity, visit <a href='{verifyPortalUrl}'>{verifyPortalUrl}</a>, 
enter your email address and the code above, then approve the request in Microsoft Authenticator.</p>
<p><em>If you did not contact the helpdesk, please ignore this message.</em></p>";

        var message = new Message
        {
            Subject = "Identity Verification Code — Helpdesk",
            Body = new ItemBody { ContentType = BodyType.Html, Content = body },
            ToRecipients = [new Recipient { EmailAddress = new EmailAddress { Address = recipientEmail } }]
        };

        await _graph.Users[senderEmail].SendMail.PostAsync(new SendMailPostRequestBody
        {
            Message = message,
            SaveToSentItems = false
        });

        _logger.LogInformation("Verification code email sent to {Email}", MaskEmail(recipientEmail));
    }

    private async Task SendTeamsMessageAsync(string recipientEmail, string displayCode, DateTime expiresAt)
    {
        var senderUserId = _config["Notifications:SenderUserId"]
            ?? throw new InvalidOperationException("Notifications:SenderUserId is not configured.");

        var verifyPortalUrl = _config["VerifyPortal:BaseUrl"]
            ?? throw new InvalidOperationException("VerifyPortal:BaseUrl is not configured.");
        var expiryText = expiresAt.ToString("HH:mm UTC");

        // Resolve recipient UPN → user ID
        var users = await _graph.Users.GetAsync(req =>
        {
            // SECURITY: Escape single quotes in the email to prevent OData filter injection.
            // OData uses single quotes as string delimiters; unescaped quotes could break the filter.
            var sanitizedEmail = recipientEmail.Replace("'", "''");
            req.QueryParameters.Filter = $"mail eq '{sanitizedEmail}'";
            req.QueryParameters.Select = ["id", "displayName"];
            req.QueryParameters.Top = 1;
        });

        var recipientUserId = users?.Value?.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException($"Could not resolve Teams user for email {MaskEmail(recipientEmail)}");

        // Create or find 1:1 chat
        var chat = await _graph.Chats.PostAsync(new Chat
        {
            ChatType = ChatType.OneOnOne,
            Members =
            [
                new AadUserConversationMember
                {
                    OdataType = "#microsoft.graph.aadUserConversationMember",
                    Roles = ["owner"],
                    AdditionalData = new Dictionary<string, object>
                    {
                        ["user@odata.bind"] = $"https://graph.microsoft.com/v1.0/users('{senderUserId}')"
                    }
                },
                new AadUserConversationMember
                {
                    OdataType = "#microsoft.graph.aadUserConversationMember",
                    Roles = ["owner"],
                    AdditionalData = new Dictionary<string, object>
                    {
                        ["user@odata.bind"] = $"https://graph.microsoft.com/v1.0/users('{recipientUserId}')"
                    }
                }
            ]
        });

        var messageText = $"Your helpdesk verification code is: **{displayCode}**\n\n" +
                          $"It expires at {expiryText}. Visit {verifyPortalUrl} to complete verification.";

        await _graph.Chats[chat!.Id].Messages.PostAsync(new ChatMessage
        {
            Body = new ItemBody { Content = messageText }
        });

        _logger.LogInformation("Verification code Teams message sent to {Email}", MaskEmail(recipientEmail));
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return "***@***";
        return email[0] + "***" + email[at..];
    }
}
