using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using VerifiedIdHelpdesk.Api.Hubs;
using VerifiedIdHelpdesk.Core.Interfaces;
using VerifiedIdHelpdesk.Infrastructure;

namespace VerifiedIdHelpdesk.Api.Controllers;

[ApiController]
[Route("api/verification")]
public class CallbackController : ControllerBase
{
    private readonly ISessionStore _sessions;
    private readonly IHubContext<VerificationHub> _hub;
    private readonly IConfiguration _config;
    private readonly ILogger<CallbackController> _logger;
    private readonly bool _requireJwtValidation;

    public CallbackController(
        ISessionStore sessions,
        IHubContext<VerificationHub> hub,
        IConfiguration config,
        ILogger<CallbackController> logger,
        IWebHostEnvironment env)
    {
        _sessions = sessions;
        _hub = hub;
        _config = config;
        _logger = logger;
        _requireJwtValidation = !env.IsEnvironment("Testing")
            && _config.GetValue<bool>("VerifiedId:RequireCallbackJwtValidation");
    }

    [HttpPost("callback")]
    [EnableRateLimiting("callback")]
    public async Task<IActionResult> Callback([FromBody] JsonElement body)
    {
        _logger.LogDebug("Callback received: {Body}", body.GetRawText());

        // Extract state (sessionId) and event type
        var requestId = body.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;
        var state = body.TryGetProperty("state", out var st) ? st.GetString() : null;
        var requestStatus = body.TryGetProperty("requestStatus", out var rs) ? rs.GetString() : null;

        if (string.IsNullOrEmpty(state))
            return BadRequest("Missing state.");

        var session = await _sessions.GetAsync(state);
        if (session == null)
        {
            _logger.LogWarning("Callback for unknown session {State}", state);
            return Ok(); // Return 200 to prevent retries
        }

        // Idempotency — already processed
        if (session.Status != "pending")
            return Ok();

        // SECURITY: Require the one-time callback token generated for this presentation request.
        // The raw token is never persisted and is only shared with the Verified ID service.
        if (!ValidateCallbackToken(session))
        {
            _logger.LogWarning("Callback rejected — invalid or missing callback token for session {SessionId}", session.SessionId);
            return Unauthorized();
        }

        // SECURITY: Correlate the callback to the specific Verified ID request when available.
        // This is a stable signal from the request API and avoids depending on the optional
        // receipt payload whose format can vary by wallet/version.
        if (!string.IsNullOrWhiteSpace(session.RequestId)
            && !string.Equals(session.RequestId, requestId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Callback rejected — requestId mismatch for session {SessionId}. Expected {ExpectedRequestId}, received {ActualRequestId}",
                session.SessionId,
                session.RequestId,
                requestId ?? "<missing>");
            return Unauthorized();
        }

        var callbackAuthMode = "token_requestid";

        // Optional hardening: when explicitly enabled, only require receipt JWT validation
        // for the successful verification callback that would transition the session to
        // "verified". Retrieval and error callbacks rely on the one-time callback token
        // and requestId correlation because receipt payloads are optional and not format-stable.
        if (_requireJwtValidation && RequiresStrictReceiptJwtValidation(requestStatus))
        {
            if (!TryGetReceiptIdToken(body, out var idToken) || !await ValidateCallbackTokenAsync(idToken))
            {
                _logger.LogWarning("Callback rejected — invalid or missing JWT for session {SessionId}", session.SessionId);
                return Unauthorized();
            }

            callbackAuthMode = "token_requestid_receiptjwt";
        }

        _logger.LogDebug(
            "Callback auth accepted: mode={CallbackAuthMode}, sessionId={SessionId}, requestStatus={RequestStatus}, requestId={RequestId}",
            callbackAuthMode,
            session.SessionId,
            requestStatus ?? "<missing>",
            requestId ?? "<missing>");

        if (requestStatus == "presentation_verified")
        {
            var verifiedClaims = ExtractClaims(body);
            session.Status = "verified";
            session.VerifiedAt = DateTime.UtcNow;
            var claimsJson = JsonSerializer.Serialize(verifiedClaims);
            // Azure Table Storage has a 64KB property limit; truncate if needed
            session.VerifiedClaims = claimsJson.Length > 30000 ? claimsJson[..30000] : claimsJson;
            await _sessions.UpdateAsync(session);

            await _hub.Clients.Group(session.SessionId).SendAsync("VerificationComplete", new
            {
                status = "verified",
                callerName = verifiedClaims.GetValueOrDefault("displayName", session.CallerDisplayName),
                employeeId = verifiedClaims.GetValueOrDefault("employeeId", string.Empty),
                department = verifiedClaims.GetValueOrDefault("department", string.Empty),
                verifiedAt = session.VerifiedAt
            });

            _logger.LogInformation("verification_completed {@Event}", new
            {
                EventName = "verification_completed",
                SessionId = session.SessionId,
                AgentEntraId = session.AgentEntraId,
                TicketId = session.TicketId
            });
        }
        else if (requestStatus == "presentation_error")
        {
            session.Status = "failed";
            await _sessions.UpdateAsync(session);

            await _hub.Clients.Group(session.SessionId).SendAsync("VerificationFailed", new
            {
                status = "failed",
                sessionId = session.SessionId
            });
        }

        return Ok();
    }

    private bool ValidateCallbackToken(VerifiedIdHelpdesk.Core.Models.VerificationSession session)
    {
        if (!Request.Headers.TryGetValue("api-key", out var providedValues))
            return false;

        var provided = providedValues.ToString();
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(session.CallbackTokenHash))
            return false;

        return CallbackTokenProtector.Matches(provided, session.CallbackTokenHash);
    }

    private static bool TryGetReceiptIdToken(JsonElement body, out string idToken)
    {
        idToken = string.Empty;

        if (!body.TryGetProperty("receipt", out var receipt)
            || !receipt.TryGetProperty("id_token", out var idTokenEl))
            return false;

        idToken = idTokenEl.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(idToken);
    }

    private static bool RequiresStrictReceiptJwtValidation(string? requestStatus)
    {
        return string.Equals(requestStatus, "presentation_verified", StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ExtractClaims(JsonElement body)
    {
        var claims = new Dictionary<string, string>();
        if (!body.TryGetProperty("verifiedCredentialsData", out var vcData)) return claims;
        foreach (var vc in vcData.EnumerateArray())
        {
            if (!vc.TryGetProperty("claims", out var claimsEl)) continue;
            foreach (var prop in claimsEl.EnumerateObject())
                claims[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }
        return claims;
    }

    private async Task<bool> ValidateCallbackTokenAsync(string token)
    {
        try
        {
            var tenantId = _config["VerifiedId:TenantId"]!;
            var metadataAddress = $"https://login.microsoftonline.com/{tenantId}/.well-known/openid-configuration";

            var configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress, new OpenIdConnectConfigurationRetriever());
            var oidcConfig = await configManager.GetConfigurationAsync();

            var handler = new JwtSecurityTokenHandler();
            // SECURITY: Issuer is validated via a custom delegate because the Verified ID
            // callback JWT may be issued from either the tenant login endpoint or the
            // Verified ID service endpoint. Audience is validated against the app's client ID.
            // Signature is validated against the tenant's signing keys.
            handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                IssuerValidator = (issuer, token, parameters) =>
                {
                    if (issuer.StartsWith("https://login.microsoftonline.com/", StringComparison.OrdinalIgnoreCase) ||
                        issuer.StartsWith("https://verifiedid.did.msidentity.com", StringComparison.OrdinalIgnoreCase))
                        return issuer;
                    throw new SecurityTokenInvalidIssuerException($"Invalid issuer: {issuer}");
                },
                ValidateAudience = true,
                ValidAudiences = [_config["AzureAd:ClientId"]!],
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = oidcConfig.SigningKeys,
                ClockSkew = TimeSpan.FromMinutes(5)
            }, out _);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Callback JWT validation failed");
            return false;
        }
    }
}
