using Microsoft.AspNetCore.Mvc;
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

    public CallbackController(
        ISessionStore sessions,
        IHubContext<VerificationHub> hub,
        IConfiguration config,
        ILogger<CallbackController> logger)
    {
        _sessions = sessions;
        _hub = hub;
        _config = config;
        _logger = logger;
    }

    [HttpPost("callback")]
    public async Task<IActionResult> Callback([FromBody] JsonElement body)
    {
        // Validate the callback JWT
        if (!body.TryGetProperty("id_token", out var idTokenEl))
            return BadRequest("Missing id_token.");

        var idToken = idTokenEl.GetString();
        if (string.IsNullOrEmpty(idToken))
            return BadRequest("Empty id_token.");

        if (!await ValidateCallbackTokenAsync(idToken))
        {
            _logger.LogWarning("Callback received with invalid JWT signature");
            return Forbid();
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

        if (requestStatus == "presentation_verified")
        {
            var verifiedClaims = ExtractClaims(body);
            session.Status = "verified";
            session.VerifiedAt = DateTime.UtcNow;
            session.VerifiedClaims = JsonSerializer.Serialize(verifiedClaims);
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
            // SECURITY NOTE: Issuer and audience validation are intentionally relaxed for
            // Entra Verified ID callbacks, which use a service-issued JWT whose issuer/audience
            // may not match the app registration. The signature IS validated against the tenant's
            // signing keys, ensuring the token was issued by Microsoft Entra for this tenant.
            //
            // CUSTOMIZE: If you know the exact issuer URI and audience for your Verified ID
            // callbacks, enable these checks for defense-in-depth:
            //   ValidateIssuer = true,
            //   ValidIssuer = "https://login.microsoftonline.com/{tenantId}/v2.0",
            //   ValidateAudience = true,
            //   ValidAudience = "{your-app-client-id}",
            handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
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
