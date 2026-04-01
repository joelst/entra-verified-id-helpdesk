using System.Globalization;
using System.Net;
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
        var effectiveChannel = NormalizeChannel(channel);

        if (!string.Equals(effectiveChannel, channel, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Teams delivery is temporarily disabled; falling back to email for {Email}",
                MaskEmail(recipientEmail));
        }

        switch (effectiveChannel)
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

    private static string NormalizeChannel(string channel)
    {
        if (string.Equals(channel, "teams", StringComparison.OrdinalIgnoreCase))
        {
            return "email";
        }

        return channel.ToLowerInvariant();
    }

    private async Task SendEmailAsync(string recipientEmail, string displayCode, DateTime expiresAt)
    {
        var senderEmail = _config["Notifications:SenderEmail"]
            ?? throw new InvalidOperationException("Notifications:SenderEmail is not configured.");

        var verifyPortalUrl = _config["VerifyPortal:BaseUrl"]
            ?? throw new InvalidOperationException("VerifyPortal:BaseUrl is required for email notifications but is not configured.");

        var expiryText = expiresAt.ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var body = $@"
<p>Your helpdesk agent has requested you verify your identity.</p>
<p><strong>Verification portal:</strong> <a href='{verifyPortalUrl}'>{verifyPortalUrl}</a></p>
<p><strong>Your one-time verification code is:</strong></p>
<h2 style='font-family:Courier New,monospace;letter-spacing:4px;'>{displayCode}</h2>
<p>This code expires at <strong>{expiryText}</strong>.</p>
<ol>
  <li>Open the verification portal link above.</li>
  <li>Enter your email address and this code.</li>
  <li>Approve the request in Microsoft Authenticator.</li>
</ol>
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
            ?? throw new InvalidOperationException("VerifyPortal:BaseUrl is required for Teams notifications.");

        // Resolve recipient UPN → user ID
        var users = await _graph.Users.GetAsync(req =>
        {
            // SECURITY: Escape single quotes in the email to prevent OData filter injection.
            // OData uses single quotes as string delimiters; unescaped quotes could break the filter.
            var sanitizedEmail = recipientEmail.Replace("'", "''");
            req.QueryParameters.Filter = $"mail eq '{sanitizedEmail}' or userPrincipalName eq '{sanitizedEmail}'";
            req.QueryParameters.Select = ["id", "displayName", "mail", "userPrincipalName"];
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

        var chatId = chat?.Id ?? throw new InvalidOperationException("Teams chat was created but no chat ID was returned.");
        var messageHtml = BuildTeamsMessageHtml(displayCode, verifyPortalUrl, expiresAt);

        await _graph.Chats[chatId].Messages.PostAsync(new ChatMessage
        {
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = messageHtml
            }
        });

        _logger.LogInformation("Verification code Teams message sent to {Email}", MaskEmail(recipientEmail));
    }

    private static string BuildTeamsMessageHtml(string displayCode, string verifyPortalUrl, DateTime expiresAt)
    {
        var expiryText = expiresAt.ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var encodedCode = WebUtility.HtmlEncode(displayCode);
        var encodedUrl = WebUtility.HtmlEncode(verifyPortalUrl);

        return $"<div>" +
               $"<p>Your helpdesk agent has requested you verify your identity.</p>" +
               $"<p><strong>Verification portal:</strong> <a href='{encodedUrl}'>{encodedUrl}</a></p>" +
               $"<p><strong>Your one-time verification code is:</strong> <strong>{encodedCode}</strong></p>" +
               $"<p>This code expires at <strong>{expiryText}</strong>.</p>" +
               $"<p>Open the portal, enter your email address and code, then approve the request in Microsoft Authenticator.</p>" +
               $"</div>";
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return "***@***";
        return email[0] + "***" + email[at..];
    }
}
