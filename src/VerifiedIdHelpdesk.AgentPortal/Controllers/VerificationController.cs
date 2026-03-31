using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;
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
    private readonly ITokenAcquisition _tokenAcquisition;
    private readonly IConfiguration _config;
    private readonly ILogger<VerificationController> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public VerificationController(
        IHttpClientFactory httpClientFactory,
        ITokenAcquisition tokenAcquisition,
        IConfiguration config,
        ILogger<VerificationController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokenAcquisition = tokenAcquisition;
        _config = config;
        _logger = logger;
    }

    // GET /Verification/Create
    [HttpGet]
    public IActionResult Create() => View();

    // POST /Verification/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    [AuthorizeForScopes(ScopeKeySection = "Api:Scopes")]
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

        var response = await client.GetAsync($"/api/verification/public-status/{sessionId}");
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
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize verified claims for session {SessionId}", sessionId);
            }
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

    // GET /Verification/Error
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Error() => View();

    // GET /Verification/History
    [HttpGet]
    public async Task<IActionResult> History()
    {
        var client = _httpClientFactory.CreateClient("ApiClient");

        var response = await client.GetAsync("/api/verification/my-sessions?limit=50");
        if (!response.IsSuccessStatusCode)
        {
            // If the API call fails (e.g. no auth), show an empty history
            return View(new HistoryViewModel { Sessions = [] });
        }

        var sessions = JsonSerializer.Deserialize<List<SessionSummary>>(
            await response.Content.ReadAsStringAsync(), JsonOptions) ?? [];

        return View(new HistoryViewModel { Sessions = sessions });
    }
    private async Task<string?> GetApiAccessTokenAsync()
    {
        var scopes = _config.GetSection("Api:Scopes").Get<string[]>();
        if (scopes is null || scopes.Length == 0)
        {
            _logger.LogWarning("Api:Scopes not configured — API calls will be unauthenticated.");
            return null;
        }

        try
        {
            return await _tokenAcquisition.GetAccessTokenForUserAsync(scopes);
        }
        catch (MicrosoftIdentityWebChallengeUserException)
        {
            throw; // Let MIWA handle the re-auth redirect
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to acquire OBO token for API");
            return null;
        }
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

public class HistoryViewModel
{
    public List<SessionSummary> Sessions { get; set; } = [];
}

public class SessionSummary
{
    public string SessionId { get; set; } = string.Empty;
    public string CallerDisplayName { get; set; } = string.Empty;
    public string CallerEmail { get; set; } = string.Empty;
    public string TicketId { get; set; } = string.Empty;
    public string DeliveryChannel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
}
