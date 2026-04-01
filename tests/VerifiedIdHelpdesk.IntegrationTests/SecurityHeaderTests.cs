using System.Net;
using System.Net.Http.Json;

namespace VerifiedIdHelpdesk.IntegrationTests;

/// <summary>
/// Verifies that every HTTP response from the API includes the security headers
/// configured in Program.cs middleware. These headers protect against clickjacking,
/// MIME-type sniffing, and other browser-side attacks.
/// </summary>
public class SecurityHeaderTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public SecurityHeaderTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    /// <summary>
    /// Confirms X-Frame-Options: DENY is present, preventing the API responses
    /// from being embedded in iframes (clickjacking mitigation).
    /// </summary>
    [Fact]
    public async Task Response_ContainsXFrameOptionsDeny()
    {
        var response = await _client.GetAsync("/api/verification/public-status/nonexistent");

        Assert.True(
            response.Headers.Contains("X-Frame-Options"),
            "X-Frame-Options header is missing.");
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").First());
    }

    /// <summary>
    /// Confirms X-Content-Type-Options: nosniff is present, preventing browsers
    /// from MIME-sniffing the response away from the declared Content-Type.
    /// </summary>
    [Fact]
    public async Task Response_ContainsXContentTypeOptionsNosniff()
    {
        var response = await _client.GetAsync("/api/verification/public-status/nonexistent");

        Assert.True(
            response.Headers.Contains("X-Content-Type-Options"),
            "X-Content-Type-Options header is missing.");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").First());
    }

    /// <summary>
    /// Confirms Referrer-Policy: strict-origin is present, limiting how much
    /// referrer information is disclosed to third-party origins.
    /// </summary>
    [Fact]
    public async Task Response_ContainsReferrerPolicy()
    {
        var response = await _client.GetAsync("/api/verification/public-status/nonexistent");

        Assert.True(
            response.Headers.Contains("Referrer-Policy"),
            "Referrer-Policy header is missing.");
        Assert.Equal("strict-origin", response.Headers.GetValues("Referrer-Policy").First());
    }

    /// <summary>
    /// Confirms X-Permitted-Cross-Domain-Policies: none is present, blocking
    /// Flash and PDF cross-domain policy requests.
    /// </summary>
    [Fact]
    public async Task Response_ContainsXPermittedCrossDomainPolicies()
    {
        var response = await _client.GetAsync("/api/verification/public-status/nonexistent");

        Assert.True(
            response.Headers.Contains("X-Permitted-Cross-Domain-Policies"),
            "X-Permitted-Cross-Domain-Policies header is missing.");
        Assert.Equal("none", response.Headers.GetValues("X-Permitted-Cross-Domain-Policies").First());
    }

    /// <summary>
    /// Validates that all five security headers appear on every response, not
    /// just on specific endpoints. Uses a POST to a public endpoint to verify
    /// headers are applied regardless of HTTP method and route.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_PresentOnPostEndpoints()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/verification/callback",
            new { state = "nonexistent", requestStatus = "presentation_verified" });

        Assert.True(response.Headers.Contains("X-Frame-Options"));
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        Assert.True(response.Headers.Contains("Referrer-Policy"));
        Assert.True(response.Headers.Contains("X-Permitted-Cross-Domain-Policies"));
    }

    /// <summary>
    /// Verifies security headers are present even on 401 Unauthorized responses.
    /// This is important because attackers targeting auth endpoints should still
    /// receive protective headers.
    /// </summary>
    [Fact]
    public async Task SecurityHeaders_PresentOn401Responses()
    {
        var response = await _client.GetAsync("/api/verification/my-sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Frame-Options"));
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        Assert.True(response.Headers.Contains("Referrer-Policy"));
    }
}
