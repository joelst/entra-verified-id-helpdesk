using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace VerifiedIdHelpdesk.VerifyPortal.Pages;

public class PresentModel : PageModel
{
    private readonly IConfiguration _config;

    public string SessionId { get; private set; } = string.Empty;
    public string QrCodeUri { get; private set; } = string.Empty;
    public string DeepLink { get; private set; } = string.Empty;
    public DateTime ExpiresAt { get; private set; } = DateTime.UtcNow.AddMinutes(10);
    public string ApiBaseUrl { get; private set; } = string.Empty;
    public string EnrollmentUrl { get; private set; } = string.Empty;

    public PresentModel(IConfiguration config) => _config = config;

    public IActionResult OnGet(string? sessionId = null)
    {
        SessionId = sessionId
                  ?? TempData["SessionId"] as string
                  ?? string.Empty;
        QrCodeUri = TempData["QrCodeUri"] as string ?? string.Empty;
        DeepLink = TempData["DeepLink"] as string ?? string.Empty;
        ApiBaseUrl = TempData["ApiBaseUrl"] as string
                   ?? _config["Api:BaseUrl"]
                   ?? string.Empty;
        EnrollmentUrl = _config["VerifiedId:EnrollmentUrl"] ?? string.Empty;

        var expiresStr = TempData["ExpiresAt"] as string;
        if (expiresStr != null && DateTime.TryParse(expiresStr, out var dt))
            ExpiresAt = dt;

        // Keep TempData alive so page refresh still works
        TempData.Keep();

        if (string.IsNullOrEmpty(SessionId))
            return RedirectToPage("Index");

        return Page();
    }
}
