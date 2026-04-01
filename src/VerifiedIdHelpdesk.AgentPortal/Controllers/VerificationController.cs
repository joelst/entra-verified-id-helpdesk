using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VerifiedIdHelpdesk.AgentPortal.Models;
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
    public IActionResult Create()
    {
        ViewBag.ApiBaseUrl = _config["Api:BaseUrl"] ?? string.Empty;
        return View();
    }

    // GET /Verification/PendingSessions — proxy to Api for JS on Create page
    [HttpGet]
    public async Task<IActionResult> PendingSessions()
    {
        var client = _httpClientFactory.CreateClient("ApiClient");
        var accessToken = await GetApiAccessTokenAsync(interactiveChallenge: false);
        if (string.IsNullOrEmpty(accessToken))
            return Json(Array.Empty<object>());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync("/api/verification/pending-sessions");
        if (!response.IsSuccessStatusCode)
            return StatusCode((int)response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        return Content(content, "application/json");
    }

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
        if (string.IsNullOrEmpty(accessToken))
        {
            ViewBag.ApiBaseUrl = _config["Api:BaseUrl"] ?? string.Empty;
            ModelState.AddModelError(string.Empty, GetTokenUnavailableMessage("create the verification request"));
            return View(model);
        }

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
            _logger.LogWarning("Create verification request failed with status code {StatusCode}", (int)response.StatusCode);
            ViewBag.ApiBaseUrl = _config["Api:BaseUrl"] ?? string.Empty;
            ModelState.AddModelError(string.Empty, GetDownstreamApiMessage(response.StatusCode, "create the verification request", error));
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
            ApiBaseUrl = _config["Api:BaseUrl"] ?? string.Empty,
            VerifyPortalUrl = _config["VerifyPortal:BaseUrl"] ?? string.Empty
        };
        return View(vm);
    }

    // GET /Verification/Result/{sessionId}
    [HttpGet]
    [AuthorizeForScopes(ScopeKeySection = "Api:Scopes")]
    public async Task<IActionResult> Result(string sessionId)
    {
        var client = _httpClientFactory.CreateClient("ApiClient");
        var accessToken = await GetApiAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken))
            return View("Error", CreateErrorViewModel(
                "Unable to load verification result",
                GetTokenUnavailableMessage("load the verification result"),
                "/Verification/Create",
                "Return to new verification"));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync($"/api/verification/status/{sessionId}");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to load verification result for session {SessionId}. Status code {StatusCode}", sessionId, (int)response.StatusCode);
            return View("Error", CreateErrorViewModel(
                "Unable to load verification result",
                GetDownstreamApiMessage(response.StatusCode, "load the verification result"),
                $"/Verification/Pending?sessionId={Uri.EscapeDataString(sessionId)}",
                "Return to pending verification"));
        }

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
    [AuthorizeForScopes(ScopeKeySection = "Api:Scopes")]
    public async Task<IActionResult> History()
    {
        var client = _httpClientFactory.CreateClient("ApiClient");
        var accessToken = await GetApiAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken))
        {
            return View(new HistoryViewModel
            {
                Sessions = [],
                ErrorMessage = GetTokenUnavailableMessage("load your verification history")
            });
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync("/api/verification/my-sessions?limit=50");
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to load verification history. Status code {StatusCode}", (int)response.StatusCode);
            return View(new HistoryViewModel
            {
                Sessions = [],
                ErrorMessage = GetDownstreamApiMessage(response.StatusCode, "load your verification history")
            });
        }

        var sessions = JsonSerializer.Deserialize<List<SessionSummary>>(
            await response.Content.ReadAsStringAsync(), JsonOptions) ?? [];

        return View(new HistoryViewModel { Sessions = sessions });
    }
    private static ErrorViewModel CreateErrorViewModel(string title, string message, string actionUrl, string actionText)
    {
        return new ErrorViewModel
        {
            Title = title,
            Message = message,
            RecoveryActionUrl = actionUrl,
            RecoveryActionText = actionText
        };
    }

    private static string GetTokenUnavailableMessage(string actionDescription)
    {
        return $"We couldn't {actionDescription} because your helpdesk session needs to be refreshed. Refresh the page and sign in again if prompted.";
    }

    private static string GetDownstreamApiMessage(HttpStatusCode statusCode, string actionDescription, string? responseBody = null)
    {
        if ((statusCode == HttpStatusCode.BadRequest || statusCode == HttpStatusCode.Conflict || (int)statusCode == 429)
            && !string.IsNullOrWhiteSpace(responseBody))
        {
            return responseBody.Trim();
        }

        return statusCode switch
        {
            HttpStatusCode.Unauthorized => $"We couldn't {actionDescription} because your sign-in to the helpdesk API has expired. Refresh the page and sign in again.",
            HttpStatusCode.Forbidden => $"We couldn't {actionDescription} because your account does not have permission for that action. Verify that you're in the Help Desk Agents group.",
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError =>
                $"We couldn't {actionDescription} because the helpdesk API is temporarily unavailable. Please try again in a moment.",
            _ => $"We couldn't {actionDescription} right now. Please try again, and contact your IT administrator if the problem continues."
        };
    }

    private async Task<string?> GetApiAccessTokenAsync(bool interactiveChallenge = true)
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
        catch (MicrosoftIdentityWebChallengeUserException) when (!interactiveChallenge)
        {
            _logger.LogInformation("Downstream API token is not available in the current user cache; skipping interactive challenge for this request.");
            return null;
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
    public string VerifyPortalUrl { get; set; } = string.Empty;
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
    public string? ErrorMessage { get; set; }
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
