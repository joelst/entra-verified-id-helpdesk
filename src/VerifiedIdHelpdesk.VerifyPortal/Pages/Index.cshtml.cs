using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace VerifiedIdHelpdesk.VerifyPortal.Pages;

public class IndexModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<IndexModel> _logger;

    public string? ErrorMessage { get; private set; }
    public string Email { get; private set; } = string.Empty;

    public IndexModel(IHttpClientFactory httpClientFactory, ILogger<IndexModel> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string email, string code)
    {
        Email = email ?? string.Empty;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
        {
            ErrorMessage = "Please enter your email address and verification code.";
            return Page();
        }

        // Normalize: strip dashes/spaces, uppercase
        var normalizedCode = code.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (normalizedCode.Length != 8)
        {
            ErrorMessage = "The code must be 8 characters. Please check and try again.";
            return Page();
        }

        var client = _httpClientFactory.CreateClient("ApiClient");

        try
        {
            var payload = JsonSerializer.Serialize(new { email = email.Trim(), code = normalizedCode });
            var response = await client.PostAsync(
                "/api/verification/initiate",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                // Never expose internal details
                ErrorMessage = "Code is invalid or has expired. Please contact your helpdesk agent.";
                return Page();
            }

            var result = await response.Content.ReadFromJsonAsync<InitiateResult>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            TempData["SessionId"] = result!.SessionId;
            TempData["QrCodeUri"] = result.QrCodeUri;
            TempData["DeepLink"] = result.DeepLink;
            TempData["ExpiresAt"] = result.ExpiresAt.ToString("O");

            return RedirectToPage("Present");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate verification for masked email.");
            ErrorMessage = "An error occurred. Please try again or contact your helpdesk agent.";
            return Page();
        }
    }
}

internal record InitiateResult(string SessionId, string? QrCodeUri, string? DeepLink, DateTime ExpiresAt);
