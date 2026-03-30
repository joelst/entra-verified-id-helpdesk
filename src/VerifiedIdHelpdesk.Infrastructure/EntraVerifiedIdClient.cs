using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VerifiedIdHelpdesk.Core.Interfaces;
using VerifiedIdHelpdesk.Core.Models;

namespace VerifiedIdHelpdesk.Infrastructure;

/// <summary>
/// Calls the Microsoft Entra Verified ID Request Service REST API.
/// SECURITY: Uses DefaultAzureCredential — no client secrets in code.
/// </summary>
public class EntraVerifiedIdClient : IVerifiedIdClient
{
    private const string VerifiedIdScope = "3db474b9-6a0c-4840-96ac-1fceb342124f/.default";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<EntraVerifiedIdClient> _logger;
    private readonly TokenCredential _credential;

    public EntraVerifiedIdClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<EntraVerifiedIdClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _credential = new DefaultAzureCredential();
    }

    public async Task<PresentationRequestResult> CreatePresentationRequestAsync(string sessionId, string callbackUrl)
    {
        var tenantId = _config["VerifiedId:TenantId"]!;
        var url = $"{tenantId}/verifiableCredentials/createPresentationRequest";

        var body = new
        {
            includeQRCode = true,
            authority = _config["VerifiedId:DidAuthority"],
            registration = new { clientName = "Identity Verification Helpdesk" },
            callback = new
            {
                url = callbackUrl,
                state = sessionId,
                headers = new { }
            },
            requestedCredentials = new[]
            {
                new
                {
                    type = _config["VerifiedId:CredentialType"],
                    acceptedIssuers = Array.Empty<string>(),
                    configuration = new
                    {
                        validation = new { allowRevoked = false, validateLinkedDomain = true }
                    }
                }
            }
        };

        var result = await PostAsync<JsonElement>(url, body);

        return new PresentationRequestResult
        {
            RequestId = result.GetProperty("requestId").GetString() ?? string.Empty,
            QrCodeUri = result.TryGetProperty("qrCode", out var qr) ? qr.GetString() ?? string.Empty : string.Empty,
            DeepLink = result.TryGetProperty("url", out var dl) ? dl.GetString() ?? string.Empty : string.Empty,
        };
    }

    private async Task<T> PostAsync<T>(string url, object body)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext([VerifiedIdScope]), CancellationToken.None);

        var baseUrl = _config["VerifiedId:RequestServiceBaseUrl"]
            ?? "https://verifiedid.did.msidentity.com/v1.0/";

        var client = _httpClientFactory.CreateClient("VerifiedIdClient");
        var json = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{url}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("VerifiedId API error {Status}: {Body}", response.StatusCode, responseBody);
            throw new HttpRequestException($"VerifiedId API returned {response.StatusCode}");
        }

        return JsonSerializer.Deserialize<T>(responseBody)!;
    }
}

