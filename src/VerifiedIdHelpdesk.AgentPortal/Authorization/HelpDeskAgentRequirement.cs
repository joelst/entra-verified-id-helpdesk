using Microsoft.AspNetCore.Authorization;

namespace VerifiedIdHelpdesk.AgentPortal.Authorization;

/// <summary>
/// Authorization requirement marker for the HelpDeskAgent policy.
/// Handled by <see cref="HelpDeskAgentHandler"/>.
/// </summary>
public class HelpDeskAgentRequirement : IAuthorizationRequirement { }
