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
        _requireJwtValidation = !env.IsEnvironment("Testing");
    }

    [HttpPost("callback")]
    [EnableRateLimiting("callback")]
    public async Task<IActionResult> Callback([FromBody] JsonElement body)
    {
        _logger.LogDebug("Callback received: {Body}", body.GetRawText());

        // SECURITY: Validate the callback JWT before mutating any session state.
        // A forged POST with a valid sessionId must not be able to transition sessions.
        var jwtValid = false;
        if (body.TryGetProperty("receipt", out var receipt)
            && receipt.TryGetProperty("id_token", out var idTokenEl))
        {
            var idToken = idTokenEl.GetString();
            if (!string.IsNullOrEmpty(idToken))
                jwtValid = await ValidateCallbackTokenAsync(idToken);
        }

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

        // SECURITY: Reject state-mutating callbacks without a valid JWT.
        // Unknown/already-processed sessions are handled above to avoid retry storms.
        // In the Testing environment, JWT validation is skipped (no real Entra tokens available).
        if (_requireJwtValidation && !jwtValid)
        {
            _logger.LogWarning("Callback rejected — invalid or missing JWT for session {SessionId}", session.SessionId);
            return Unauthorized();
        }

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
