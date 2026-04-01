using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VerifiedIdHelpdesk.Core.Interfaces;
using VerifiedIdHelpdesk.Core.Models;

namespace VerifiedIdHelpdesk.Infrastructure;

/// <summary>
/// Calls the Microsoft Entra Verified ID Request Service REST API.
/// SECURITY: Uses the app registration certificate (from Key Vault) to authenticate
/// via client credentials flow. Managed identity alone does not carry the required
/// VerifiableCredential.Create.All role in the Verified ID access token.
/// </summary>
public class EntraVerifiedIdClient : IVerifiedIdClient
{
    private const string VerifiedIdScope = "3db474b9-6a0c-4840-96ac-1fceb342124f/.default";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<EntraVerifiedIdClient> _logger;
    private readonly Lazy<Task<TokenCredential>> _credentialLazy;

    public EntraVerifiedIdClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<EntraVerifiedIdClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _credentialLazy = new Lazy<Task<TokenCredential>>(LoadCertificateCredentialAsync);
    }

    private async Task<TokenCredential> LoadCertificateCredentialAsync()
    {
        var tenantId = _config["VerifiedId:TenantId"]!;
        var clientId = _config["VerifiedId:ClientId"]!;
        var kvUrl = _config["AzureAd:ClientCertificates:0:KeyVaultUrl"]!;
        var certName = _config["AzureAd:ClientCertificates:0:KeyVaultCertificateName"]!;

        var certClient = new CertificateClient(new Uri(kvUrl), new DefaultAzureCredential());
        var certResponse = await certClient.DownloadCertificateAsync(certName);
        var cert = certResponse.Value;

        _logger.LogInformation(
            "Loaded certificate {Thumbprint} for Verified ID client credentials",
            cert.Thumbprint);

        return new ClientCertificateCredential(tenantId, clientId, cert);
    }

    public async Task<PresentationRequestResult> CreatePresentationRequestAsync(string sessionId, string callbackUrl, string callbackApiKey)
    {
        var url = "verifiableCredentials/createPresentationRequest";
        var callbackHeaders = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(callbackApiKey))
            callbackHeaders["api-key"] = callbackApiKey;

        var body = new
        {
            includeQRCode = true,
            includeReceipt = true,
            authority = _config["VerifiedId:DidAuthority"],
            registration = new { clientName = "Identity Verification Helpdesk" },
            callback = new
            {
                url = callbackUrl,
                state = sessionId,
                headers = callbackHeaders
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
        var credential = await _credentialLazy.Value;
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([VerifiedIdScope]), CancellationToken.None);

        var baseUrl = _config["VerifiedId:RequestServiceBaseUrl"]
            ?? "https://verifiedid.did.msidentity.com/v1.0/";

        var client = _httpClientFactory.CreateClient("VerifiedIdClient");
        var json = JsonSerializer.Serialize(body);
        var fullUrl = $"{baseUrl}{url}";

        _logger.LogDebug("VerifiedId API request: POST {Url}", fullUrl);

        using var request = new HttpRequestMessage(HttpMethod.Post, fullUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "VerifiedId API error {Status} for {Url}: {Body}",
                response.StatusCode, fullUrl, responseBody);
            throw new HttpRequestException($"VerifiedId API returned {response.StatusCode}");
        }

        return JsonSerializer.Deserialize<T>(responseBody)!;
    }
}

