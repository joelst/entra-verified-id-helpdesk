namespace VerifiedIdHelpdesk.AgentPortal.Models;

public class ErrorViewModel
{
    public string? Title { get; set; }
    public string? Message { get; set; }
    public string? RecoveryActionText { get; set; }
    public string? RecoveryActionUrl { get; set; }
    public string? RequestId { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
