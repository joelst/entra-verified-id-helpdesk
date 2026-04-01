using System.Net;
using System.Text;

namespace VerifiedIdHelpdesk.IntegrationTests;

/// <summary>
/// Verifies that the API enforces authentication on protected endpoints and
/// allows unauthenticated access to public endpoints. These tests run against
/// the full HTTP pipeline (routing, middleware, filters) without real Azure
/// credentials.
/// </summary>
public class AuthorizationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AuthorizationTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ── Protected endpoints ──────────────────────────────────────────────────

    /// <summary>
    /// Endpoints decorated with [Authorize(Policy = "HelpDeskAgent")] must reject
    /// requests that carry no bearer token. Returning 401 prevents unauthenticated
    /// callers from generating codes, viewing sessions, or accessing agent-only data.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/verification/generate")]
    [InlineData("GET", "/api/verification/status/test-session-id")]
    [InlineData("GET", "/api/verification/my-sessions")]
    [InlineData("GET", "/api/verification/pending-sessions")]
    public async Task ProtectedEndpoint_WithoutAuth_ReturnsUnauthorized(string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method == "POST")
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The directory search endpoint requires authentication (any valid Entra ID
    /// token, not necessarily a helpdesk agent). It must also return 401 without
    /// a token.
    /// </summary>
    [Fact]
    public async Task DirectorySearch_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/directory/search?q=test");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Public endpoints ─────────────────────────────────────────────────────

    /// <summary>
    /// Public endpoints used by the caller's browser (initiate, public-status)
    /// and the Entra Verified ID webhook (callback) must not require authentication.
    /// A non-401 response confirms the request reaches the controller logic.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/verification/initiate")]
    [InlineData("POST", "/api/verification/callback")]
    [InlineData("GET", "/api/verification/public-status/some-session-id")]
    public async Task PublicEndpoint_WithoutAuth_DoesNotReturn401(string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method == "POST")
        {
            request.Content = new StringContent(
                """{"email":"test@test.com","code":"ABCD1234"}""",
                Encoding.UTF8,
                "application/json");
        }

        var response = await _client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
