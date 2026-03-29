using Microsoft.AspNetCore.Authorization;
using Microsoft.Graph;
using System.Security.Claims;
using CoreConstants = VerifiedIdHelpdesk.Core.Constants;

namespace VerifiedIdHelpdesk.AgentPortal.Authorization;

/// <summary>
/// Evaluates whether the current user belongs to the HelpDeskAgents Entra group.
///
/// SECURITY — Group claim overage: When a user belongs to more than 200 Entra groups,
/// Azure silently omits the 'groups' claim from the token and replaces it with a
/// '_claim_names' overage indicator. In that case, reading the token claim directly
/// will ALWAYS fail, locking out legitimate agents in large organizations.
///
/// This handler detects overage and falls back to a direct Microsoft Graph API call
/// (checkMemberGroups) to verify group membership without relying on the token claim.
///
/// CUSTOMIZE: Update "AuthorizationGroups:HelpDeskAgents" in appsettings.json with
/// your Entra security group's Object ID.
/// </summary>
public class HelpDeskAgentHandler : AuthorizationHandler<HelpDeskAgentRequirement>
{
    private readonly GraphServiceClient _graph;
    private readonly IConfiguration _config;
    private readonly ILogger<HelpDeskAgentHandler> _logger;

    public HelpDeskAgentHandler(
        GraphServiceClient graph,
        IConfiguration config,
        ILogger<HelpDeskAgentHandler> logger)
    {
        _graph = graph;
        _config = config;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        HelpDeskAgentRequirement requirement)
    {
        var groupId = _config["AuthorizationGroups:HelpDeskAgents"];
        if (string.IsNullOrEmpty(groupId))
        {
            _logger.LogWarning("AuthorizationGroups:HelpDeskAgents is not configured.");
            return; // Deny
        }

        // Check for group claim overage (user in 200+ groups — token groups claim was truncated)
        if (context.User.HasClaim(c => c.Type == "_claim_names"))
        {
            await HandleOverageCaseAsync(context, requirement, groupId);
            return;
        }

        // Normal path: read the groups claim directly from the token
        if (context.User.HasClaim("groups", groupId))
        {
            context.Succeed(requirement);
        }
    }

    /// <summary>
    /// Falls back to Graph API when the token groups claim was truncated due to overage.
    /// Requires the GroupMember.Read.All application permission on the app registration.
    /// </summary>
    private async Task HandleOverageCaseAsync(
        AuthorizationHandlerContext context,
        HelpDeskAgentRequirement requirement,
        string groupId)
    {
        var userId = context.User.FindFirstValue("oid")
                  ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Cannot determine user OID for group overage check.");
            return;
        }

        try
        {
            var result = await _graph.Users[userId].CheckMemberGroups.PostAsCheckMemberGroupsPostResponseAsync(
                new Microsoft.Graph.Users.Item.CheckMemberGroups.CheckMemberGroupsPostRequestBody
                {
                    GroupIds = [groupId]
                });

            if (result?.Value?.Count > 0)
            {
                context.Succeed(requirement);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Graph group membership check failed for user {UserId}", userId);
            // Fail silently — the user is denied access
        }
    }
}
