namespace VerifiedIdHelpdesk.Core.Models;

public class IssuanceRequestResult
{
    public string RequestId { get; set; } = string.Empty;
    public string QrCodeUri { get; set; } = string.Empty;
    public string DeepLink { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}
