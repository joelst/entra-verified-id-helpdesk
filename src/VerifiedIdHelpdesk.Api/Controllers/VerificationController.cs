using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using System.Text.Json;
using VerifiedIdHelpdesk.Api.Hubs;
using VerifiedIdHelpdesk.Core;
using VerifiedIdHelpdesk.Core.Interfaces;
using VerifiedIdHelpdesk.Core.Models;
using VerifiedIdHelpdesk.Infrastructure;

namespace VerifiedIdHelpdesk.Api.Controllers;

[ApiController]
[Route("api/verification")]
public class VerificationController : ControllerBase
{
    private readonly ISessionStore _sessions;
    private readonly IVerifiedIdClient _verifiedId;
    private readonly INotificationService _notifications;
    private readonly IHubContext<VerificationHub> _hub;
    private readonly IConfiguration _config;
    private readonly ILogger<VerificationController> _logger;

    public VerificationController(
        ISessionStore sessions,
        IVerifiedIdClient verifiedId,
        INotificationService notifications,
        IHubContext<VerificationHub> hub,
        IConfiguration config,
        ILogger<VerificationController> logger)
    {
        _sessions = sessions;
        _verifiedId = verifiedId;
        _notifications = notifications;
        _hub = hub;
        _config = config;
        _logger = logger;
    }

    [HttpPost("generate")]
    [Authorize(Policy = Constants.HelpDeskAgentPolicy)]
    public async Task<IActionResult> Generate([FromBody] GenerateRequest request)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (string.IsNullOrWhiteSpace(request.CallerEmail)) return BadRequest("CallerEmail is required.");
        if (string.IsNullOrWhiteSpace(request.CallerEntraId)) return BadRequest("CallerEntraId is required.");
        if (string.IsNullOrWhiteSpace(request.DeliveryChannel)) return BadRequest("DeliveryChannel is required.");
        if (!string.IsNullOrEmpty(request.Note) && request.Note.Length > 500)
            return BadRequest("Note must be 500 characters or fewer.");

        var agentEntraId = User.FindFirstValue("oid") ?? User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var agentDisplayName = User.FindFirstValue("name") ?? User.Identity?.Name ?? agentEntraId;

        var pendingCount = await _sessions.CountPendingByAgentAsync(agentEntraId);
        if (pendingCount >= Constants.MaxPendingSessionsPerAgent)
            return BadRequest($"You have {pendingCount} pending verifications. Maximum is {Constants.MaxPendingSessionsPerAgent}.");

        var code = CodeGenerator.Generate();
        var hmacKey = _config["HmacKey"]!;
        var codeHash = CodeHasher.Hash(code, hmacKey);
        var now = DateTime.UtcNow;

        var session = new VerificationSession
        {
            SessionId = Guid.NewGuid().ToString(),
            CodeHash = codeHash,
            CallerEmail = request.CallerEmail.Trim().ToLowerInvariant(),
            CallerEntraId = request.CallerEntraId,
            CallerDisplayName = request.CallerDisplayName ?? string.Empty,
            TicketId = request.TicketId ?? string.Empty,
            Note = request.Note ?? string.Empty,
            AgentEntraId = agentEntraId,
            AgentDisplayName = agentDisplayName,
            DeliveryChannel = request.DeliveryChannel.ToLowerInvariant(),
            Status = "pending",
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(Constants.CodeExpiryMinutes)
        };

        await _sessions.CreateAsync(session);

        var displayCode = CodeGenerator.FormatForDisplay(code);

        // Skip sending notification for verbal delivery — agent reads the code to the caller directly
        if (session.DeliveryChannel != Constants.ChannelVerbal)
        {
            await _notifications.SendCodeAsync(request.CallerEmail, displayCode, session.ExpiresAt, session.DeliveryChannel);
        }

        _logger.LogInformation("code_generated {@Event}", new
        {
            EventName = "code_generated",
            SessionId = session.SessionId,
            AgentEntraId = agentEntraId,
            AgentDisplayName = agentDisplayName,
            CallerEntraId = session.CallerEntraId,
            CallerEmail = session.CallerEmail,
            TicketId = session.TicketId,
            DeliveryChannel = session.DeliveryChannel,
            ExpiresAt = session.ExpiresAt
        });

        return Ok(new
        {
            sessionId = session.SessionId,
            code,           // plaintext — shown to agent only, never logged
            displayCode,
            expiresAt = session.ExpiresAt
        });
    }

    [HttpPost("initiate")]
    public async Task<IActionResult> Initiate([FromBody] InitiateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Code))
            return BadRequest("Email and code are required.");

        // Normalize
        var normalizedCode = request.Code.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (normalizedCode.Length != Constants.CodeLength)
            return BadRequest("Invalid code format.");

        var hmacKey = _config["HmacKey"]!;
        var codeHash = CodeHasher.Hash(normalizedCode, hmacKey);

        var session = await _sessions.GetByCodeHashAsync(codeHash, request.Email.Trim().ToLowerInvariant());
        if (session == null || session.Status != "pending" || session.ExpiresAt <= DateTime.UtcNow)
            return BadRequest("Code is invalid or has expired.");

        session.FailedAttempts++;
        if (session.FailedAttempts > Constants.MaxFailedAttempts)
        {
            session.Status = "failed";
            await _sessions.UpdateAsync(session);
            return BadRequest("Too many failed attempts.");
        }

        var apiBaseUrl = _config["Api:BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}";
        var callbackUrl = $"{apiBaseUrl}/api/verification/callback";

        PresentationRequestResult result;
        try
        {
            result = await _verifiedId.CreatePresentationRequestAsync(session.SessionId, callbackUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create presentation request for session {SessionId}", session.SessionId);
            return StatusCode(500, "Failed to initiate verification.");
        }

        session.RequestId = result.RequestId;
        await _sessions.UpdateAsync(session);

        _logger.LogInformation("verification_initiated {@Event}", new
        {
            EventName = "verification_initiated",
            SessionId = session.SessionId,
            CallerEmail = MaskEmail(request.Email)
        });

        return Ok(new
        {
            sessionId = session.SessionId,
            requestId = result.RequestId,
            qrCodeUri = result.QrCodeUri,
            deepLink = result.DeepLink,
            expiresAt = session.ExpiresAt
        });
    }

    [HttpGet("status/{sessionId}")]
    [Authorize(Policy = Constants.HelpDeskAgentPolicy)]
    public async Task<IActionResult> Status(string sessionId)
    {
        var session = await _sessions.GetAsync(sessionId);
        if (session == null) return NotFound();

        return Ok(new
        {
            status = session.Status,
            verifiedClaims = session.VerifiedClaims,
            verifiedAt = session.VerifiedAt
        });
    }

    [HttpGet("public-status/{sessionId}")]
    public async Task<IActionResult> PublicStatus(string sessionId)
    {
        var session = await _sessions.GetAsync(sessionId);
        if (session == null) return NotFound();

        // Return status + verified claims (sessionId is a non-guessable GUID)
        return Ok(new
        {
            status = session.Status,
            verifiedClaims = session.VerifiedClaims,
            verifiedAt = session.VerifiedAt
        });
    }

    [HttpGet("my-sessions")]
    [Authorize(Policy = Constants.HelpDeskAgentPolicy)]
    public async Task<IActionResult> MySessions([FromQuery] int limit = 50)
    {
        var agentEntraId = User.FindFirstValue("oid") ?? User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        var sessions = await _sessions.GetByAgentAsync(agentEntraId, Math.Min(limit, 100));

        var result = sessions.Select(s => new
        {
            sessionId = s.SessionId,
            callerDisplayName = s.CallerDisplayName,
            callerEmail = s.CallerEmail,
            ticketId = s.TicketId,
            deliveryChannel = s.DeliveryChannel,
            status = s.Status,
            createdAt = s.CreatedAt,
            verifiedAt = s.VerifiedAt
        });

        return Ok(result);
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return "***@***";
        return email[0] + "***" + email[at..];
    }
}

public record GenerateRequest(
    string? CallerEntraId,
    string? CallerEmail,
    string? CallerDisplayName,
    string? TicketId,
    string? Note,
    string? DeliveryChannel);

public record InitiateRequest(string Email, string Code);
