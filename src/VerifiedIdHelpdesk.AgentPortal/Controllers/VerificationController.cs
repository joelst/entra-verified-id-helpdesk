using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CoreConstants = VerifiedIdHelpdesk.Core.Constants;

namespace VerifiedIdHelpdesk.AgentPortal.Controllers;

/// <summary>
/// Handles the helpdesk agent verification workflow:
/// Create → Pending → Result
/// </summary>
[Authorize(Policy = CoreConstants.HelpDeskAgentPolicy)]
public class VerificationController : Controller
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<VerificationController> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public VerificationController(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<VerificationController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    // GET /Verification/Create
    [HttpGet]
    public IActionResult Create() => View();

    // POST /Verification/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateVerificationViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var client = _httpClientFactory.CreateClient("ApiClient");

        // Forward the user's bearer token to the API
        var accessToken = await GetApiAccessTokenAsync();
        if (!string.IsNullOrEmpty(accessToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var payload = JsonSerializer.Serialize(new
        {
            callerEntraId = model.CallerEntraId,
            callerEmail = model.CallerEmail,
            callerDisplayName = model.CallerDisplayName,
            ticketId = model.TicketId ?? string.Empty,
            note = model.Note ?? string.Empty,
            deliveryChannel = model.DeliveryChannel
        });

        var response = await client.PostAsync(
            "/api/verification/generate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            ModelState.AddModelError(string.Empty, $"Failed to create verification: {error}");
            return View(model);
        }

        var result = JsonSerializer.Deserialize<GenerateResponse>(
            await response.Content.ReadAsStringAsync(), JsonOptions);

        return RedirectToAction("Pending", new
        {
            sessionId = result!.SessionId,
            displayCode = result.DisplayCode,
            callerDisplayName = model.CallerDisplayName,
            ticketId = model.TicketId,
            deliveryChannel = model.DeliveryChannel,
            expiresAt = result.ExpiresAt.ToString("o")
        });
    }

    // GET /Verification/Pending/{sessionId}
    [HttpGet]
    public IActionResult Pending(
        string sessionId,
        string displayCode,
        string? callerDisplayName,
        string? ticketId,
        string? deliveryChannel,
        string? expiresAt)
    {
        var vm = new PendingViewModel
        {
            SessionId = sessionId,
            DisplayCode = displayCode,
            CallerDisplayName = callerDisplayName ?? "Unknown",
            TicketId = ticketId ?? string.Empty,
            DeliveryChannel = deliveryChannel ?? "email",
            ExpiresAt = DateTime.TryParse(expiresAt, out var dt) ? dt : DateTime.UtcNow.AddMinutes(10),
            ApiBaseUrl = _config["Api:BaseUrl"] ?? string.Empty
        };
        return View(vm);
    }

    // GET /Verification/Result/{sessionId}
    [HttpGet]
    public async Task<IActionResult> Result(string sessionId)
    {
        var client = _httpClientFactory.CreateClient("ApiClient");
        var accessToken = await GetApiAccessTokenAsync();
        if (!string.IsNullOrEmpty(accessToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync($"/api/verification/status/{sessionId}");
        if (!response.IsSuccessStatusCode)
            return RedirectToAction("Create");

        var status = JsonSerializer.Deserialize<StatusResponse>(
            await response.Content.ReadAsStringAsync(), JsonOptions);

        if (status?.Status != "verified")
            return RedirectToAction("Pending", new { sessionId });

        Dictionary<string, string>? claims = null;
        if (!string.IsNullOrEmpty(status.VerifiedClaims))
        {
            try
            {
                claims = JsonSerializer.Deserialize<Dictionary<string, string>>(status.VerifiedClaims, JsonOptions);
            }
            catch { /* Ignore deserialization errors */ }
        }

        var vm = new ResultViewModel
        {
            SessionId = sessionId,
            VerifiedAt = status.VerifiedAt ?? DateTime.UtcNow,
            CallerName = claims?.GetValueOrDefault("displayName", "Unknown") ?? "Unknown",
            EmployeeId = claims?.GetValueOrDefault("employeeId", string.Empty) ?? string.Empty,
            Department = claims?.GetValueOrDefault("department", string.Empty) ?? string.Empty
        };

        return View(vm);
    }

    // GET /Verification/AccessDenied
    [HttpGet]
    [AllowAnonymous]
    public IActionResult AccessDenied() => View();

    private static Task<string?> GetApiAccessTokenAsync()
    {
        // CUSTOMIZE: In production, acquire a proper OBO token using ITokenAcquisition.
        // For the sample, the AgentPortal uses its own identity to call the API.
        // See: https://learn.microsoft.com/en-us/azure/active-directory/develop/v2-oauth2-on-behalf-of-flow
        return Task.FromResult<string?>(null); // Token acquisition wired via Microsoft.Identity.Web in production
    }
}

// ── View Models ───────────────────────────────────────────────────────────

public class CreateVerificationViewModel
{
    public string CallerEntraId { get; set; } = string.Empty;
    public string CallerEmail { get; set; } = string.Empty;
    public string CallerDisplayName { get; set; } = string.Empty;
    public string? TicketId { get; set; }
    public string? Note { get; set; }
    public string DeliveryChannel { get; set; } = "email";
}

public class PendingViewModel
{
    public string SessionId { get; set; } = string.Empty;
    public string DisplayCode { get; set; } = string.Empty;
    public string CallerDisplayName { get; set; } = string.Empty;
    public string TicketId { get; set; } = string.Empty;
    public string DeliveryChannel { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string ApiBaseUrl { get; set; } = string.Empty;
}

public class ResultViewModel
{
    public string SessionId { get; set; } = string.Empty;
    public DateTime VerifiedAt { get; set; }
    public string CallerName { get; set; } = string.Empty;
    public string EmployeeId { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
}

public record GenerateResponse(string SessionId, string DisplayCode, DateTime ExpiresAt);
public record StatusResponse(string Status, string? VerifiedClaims, DateTime? VerifiedAt);
