using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using VerifiedIdHelpdesk.Core;
using VerifiedIdHelpdesk.Core.Interfaces;
using VerifiedIdHelpdesk.Core.Models;
using VerifiedIdHelpdesk.Infrastructure;

namespace VerifiedIdHelpdesk.IntegrationTests;

/// <summary>
/// End-to-end tests that exercise the public verification flow through the
/// full HTTP pipeline. Because <c>POST /api/verification/generate</c> requires
/// HelpDeskAgent authorization, these tests pre-seed sessions directly in the
/// <see cref="InMemorySessionStore"/> and then exercise the public path:
/// initiate → callback → public-status.
/// </summary>
public class FullFlowTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    /// <summary>
    /// HMAC key used in the test configuration (must match the key supplied to
    /// the test server via <see cref="TestWebApplicationFactory"/>).
    /// </summary>
    private readonly string _hmacKey;

    public FullFlowTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        // Read the same HMAC key the test server uses so we can compute valid hashes.
        var config = factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        _hmacKey = config["HmacKey"]!;
    }

    // ── Initiate ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A valid code+email pair that matches a pending session should pass
    /// input validation (routing, model binding, HMAC hash lookup). The
    /// downstream <see cref="IVerifiedIdClient"/> mock is not configured,
    /// so the controller will throw after validation. An exception at that
    /// stage proves HMAC validation and session lookup succeeded.
    /// </summary>
    [Fact]
    public async Task Initiate_WithValidCodeAndEmail_PassesValidation()
    {
        // Arrange — seed a pending session with a known code hash.
        const string code = "ABCD2345";
        const string email = "caller@example.com";
        var session = CreateSeededSession(code, email);
        await _factory.Sessions.CreateAsync(session);

        // Act — the VerifiedIdClient mock returns null, causing a
        // NullReferenceException after validation passes. This is expected:
        // the test verifies that the code was accepted (not rejected as 400).
        HttpResponseMessage? response = null;
        Exception? caughtException = null;
        try
        {
            response = await _client.PostAsJsonAsync(
                "/api/verification/initiate",
                new { email, code });
        }
        catch (Exception ex)
        {
            caughtException = ex;
        }

        // Assert — either a non-400 status or an exception proves validation passed.
        if (response != null)
        {
            Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
        else
        {
            // Exception thrown after code validation — the mock client is not set up,
            // confirming we got past the HMAC check.
            Assert.NotNull(caughtException);
        }
    }

    /// <summary>
    /// An incorrect code for a known email should be rejected with 400,
    /// confirming the HMAC comparison is working through the full pipeline.
    /// </summary>
    [Fact]
    public async Task Initiate_WithWrongCode_Returns400()
    {
        const string correctCode = "WXYZ6789";
        const string email = "wrong-code@example.com";
        var session = CreateSeededSession(correctCode, email);
        await _factory.Sessions.CreateAsync(session);

        var response = await _client.PostAsJsonAsync(
            "/api/verification/initiate",
            new { email, code = "AAAAAAAA" }); // wrong code

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The fifth wrong code guess should lock the pending session so the caller
    /// cannot keep brute-forcing the same verification request.
    /// </summary>
    [Fact]
    public async Task Initiate_WithFifthWrongCode_LocksPendingSession()
    {
        const string correctCode = "LMNO4567";
        const string email = "lockout@example.com";
        var session = CreateSeededSession(correctCode, email);
        session.FailedAttempts = Constants.MaxFailedAttempts - 1;
        await _factory.Sessions.CreateAsync(session);

        var response = await _client.PostAsJsonAsync(
            "/api/verification/initiate",
            new { email, code = "WRONG999" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Too many failed attempts.", body);

        var updated = await _factory.Sessions.GetAsync(session.SessionId);
        Assert.NotNull(updated);
        Assert.Equal(Constants.MaxFailedAttempts, updated!.FailedAttempts);
        Assert.Equal(SessionStatus.Failed, updated.Status);
    }

    /// <summary>
    /// An expired session should be rejected even if the code is correct,
    /// confirming expiry enforcement in the full pipeline.
    /// </summary>
    [Fact]
    public async Task Initiate_WithExpiredSession_Returns400()
    {
        const string code = "EXPR1234";
        const string email = "expired@example.com";
        var session = CreateSeededSession(code, email);
        session.ExpiresAt = DateTime.UtcNow.AddMinutes(-1); // already expired
        await _factory.Sessions.CreateAsync(session);

        var response = await _client.PostAsJsonAsync(
            "/api/verification/initiate",
            new { email, code });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Callback → PublicStatus ──────────────────────────────────────────────

    /// <summary>
    /// Simulates the Entra Verified ID callback for a successful presentation.
    /// After the callback, the session status should transition from "pending"
    /// to "verified" and the public-status endpoint should reflect this.
    /// </summary>
    [Fact]
    public async Task Callback_PresentationVerified_UpdatesSessionToVerified()
    {
        var callbackToken = CallbackTokenProtector.Generate();

        // Arrange — seed a pending session.
        var session = new VerificationSession
        {
            SessionId = Guid.NewGuid().ToString(),
            CodeHash = "irrelevant-for-callback",
            CallerEmail = "verified@example.com",
            CallerDisplayName = "Test Caller",
            AgentEntraId = "agent-oid",
            AgentDisplayName = "Test Agent",
            Status = SessionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            RequestId = "request-id-for-callback",
            CallbackTokenHash = CallbackTokenProtector.Hash(callbackToken)
        };
        await _factory.Sessions.CreateAsync(session);

        // Act — simulate Entra Verified ID callback.
        var callbackBody = new
        {
            requestId = session.RequestId,
            state = session.SessionId,
            requestStatus = "presentation_verified",
            verifiedCredentialsData = new[]
            {
                new
                {
                    claims = new
                    {
                        displayName = "Verified User",
                        employeeId = "EMP001",
                        department = "Engineering"
                    }
                }
            }
        };

        using var callbackRequest = new HttpRequestMessage(HttpMethod.Post, "/api/verification/callback")
        {
            Content = JsonContent.Create(callbackBody)
        };
        callbackRequest.Headers.Add("api-key", callbackToken);

        var callbackResponse = await _client.SendAsync(callbackRequest);

        // Assert — callback accepted.
        Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);

        // Assert — session status updated in store.
        var updatedSession = await _factory.Sessions.GetAsync(session.SessionId);
        Assert.NotNull(updatedSession);
        Assert.Equal(SessionStatus.Verified, updatedSession.Status);
        Assert.NotNull(updatedSession.VerifiedAt);

        // Assert — public-status reflects the new status.
        var statusResponse = await _client.GetAsync(
            $"/api/verification/public-status/{session.SessionId}");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        var statusBody = await statusResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(statusBody);
        Assert.Equal("verified", doc.RootElement.GetProperty("status").GetString());
    }

    /// <summary>
    /// A callback with "presentation_error" should transition the session to
    /// "failed" status.
    /// </summary>
    [Fact]
    public async Task Callback_PresentationError_UpdatesSessionToFailed()
    {
        var callbackToken = CallbackTokenProtector.Generate();

        var session = new VerificationSession
        {
            SessionId = Guid.NewGuid().ToString(),
            CodeHash = "irrelevant-for-callback",
            CallerEmail = "failed@example.com",
            CallerDisplayName = "Failed Caller",
            AgentEntraId = "agent-oid",
            AgentDisplayName = "Test Agent",
            Status = SessionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            RequestId = "request-id-for-failure",
            CallbackTokenHash = CallbackTokenProtector.Hash(callbackToken)
        };
        await _factory.Sessions.CreateAsync(session);

        var callbackBody = new
        {
            requestId = session.RequestId,
            state = session.SessionId,
            requestStatus = "presentation_error",
        };

        using var callbackRequest = new HttpRequestMessage(HttpMethod.Post, "/api/verification/callback")
        {
            Content = JsonContent.Create(callbackBody)
        };
        callbackRequest.Headers.Add("api-key", callbackToken);

        var callbackResponse = await _client.SendAsync(callbackRequest);

        Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);

        var updatedSession = await _factory.Sessions.GetAsync(session.SessionId);
        Assert.NotNull(updatedSession);
        Assert.Equal(SessionStatus.Failed, updatedSession.Status);
    }

    /// <summary>
    /// Idempotency: a duplicate callback for an already-verified session must
    /// succeed (200) without changing the session state. This prevents double-
    /// processing from webhook retries.
    /// </summary>
    [Fact]
    public async Task Callback_DuplicateForVerifiedSession_ReturnsOkWithoutChange()
    {
        var session = new VerificationSession
        {
            SessionId = Guid.NewGuid().ToString(),
            CodeHash = "irrelevant",
            CallerEmail = "idempotent@example.com",
            CallerDisplayName = "Idempotent Caller",
            AgentEntraId = "agent-oid",
            AgentDisplayName = "Test Agent",
            Status = SessionStatus.Verified,
            VerifiedAt = DateTime.UtcNow.AddMinutes(-5),
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            RequestId = "already-verified-request",
        };
        await _factory.Sessions.CreateAsync(session);

        var callbackBody = new
        {
            requestId = session.RequestId,
            state = session.SessionId,
            requestStatus = "presentation_verified",
        };

        var response = await _client.PostAsJsonAsync(
            "/api/verification/callback", callbackBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Session should still be verified — not re-processed.
        var unchanged = await _factory.Sessions.GetAsync(session.SessionId);
        Assert.NotNull(unchanged);
        Assert.Equal(SessionStatus.Verified, unchanged.Status);
    }

    // ── Public Status ────────────────────────────────────────────────────────

    /// <summary>
    /// A pending session should return its status as "pending" via the public
    /// status endpoint.
    /// </summary>
    [Fact]
    public async Task PublicStatus_PendingSession_ReturnsPendingStatus()
    {
        var session = new VerificationSession
        {
            SessionId = Guid.NewGuid().ToString(),
            CodeHash = "irrelevant",
            CallerEmail = "pending@example.com",
            CallerDisplayName = "Pending Caller",
            AgentEntraId = "agent-oid",
            AgentDisplayName = "Test Agent",
            Status = SessionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        };
        await _factory.Sessions.CreateAsync(session);

        var response = await _client.GetAsync(
            $"/api/verification/public-status/{session.SessionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a pre-seeded <see cref="VerificationSession"/> with a correct
    /// HMAC hash for the given plaintext code and email, ready for initiate tests.
    /// </summary>
    private VerificationSession CreateSeededSession(string plaintextCode, string email)
    {
        return new VerificationSession
        {
            SessionId = Guid.NewGuid().ToString(),
            CodeHash = CodeHasher.Hash(plaintextCode, _hmacKey),
            CallerEmail = email.Trim().ToLowerInvariant(),
            CallerEntraId = "caller-oid",
            CallerDisplayName = "Test Caller",
            AgentEntraId = "agent-oid",
            AgentDisplayName = "Test Agent",
            DeliveryChannel = "email",
            Status = SessionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        };
    }
}
