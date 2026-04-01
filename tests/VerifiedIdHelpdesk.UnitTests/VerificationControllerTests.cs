using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Claims;
using VerifiedIdHelpdesk.Api.Controllers;
using VerifiedIdHelpdesk.Api.Hubs;
using VerifiedIdHelpdesk.Core;
using VerifiedIdHelpdesk.Core.Interfaces;
using VerifiedIdHelpdesk.Core.Models;

namespace VerifiedIdHelpdesk.UnitTests;

/// <summary>
/// Unit tests for <see cref="VerificationController"/>.
///
/// The controller is instantiated directly (no HTTP middleware) with mocked
/// dependencies so each test exercises only the controller logic. Auth policy
/// enforcement is not tested here — that belongs in integration tests.
///
/// Security significance: The Generate endpoint must never return the code hash
/// to the agent (only plaintext). The Initiate endpoint must enforce expiry,
/// status, and attempt limits. PublicStatus must not leak claim data.
/// </summary>
public class VerificationControllerTests
{
    // Shared mock instances — xUnit creates a new class instance per test,
    // so these are fresh for every test method.
    private readonly Mock<ISessionStore> _sessionStore = new();
    private readonly Mock<IVerifiedIdClient> _verifiedIdClient = new();
    private readonly Mock<INotificationService> _notifications = new();
    private readonly Mock<IHubContext<VerificationHub>> _hub = new();

    /// <summary>
    /// Creates a controller instance wired with the shared mocks and a fake
    /// authenticated agent user.
    /// </summary>
    private VerificationController CreateController(string agentOid = "agent-oid-123")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Fixed test HMAC key: 32 zero bytes in base64
                ["HmacKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["Api:BaseUrl"] = "https://api.test"
            })
            .Build();

        // Wire hub mock chain so any call to Clients.Group(...).SendAsync(...) succeeds.
        var mockClients = new Mock<IHubClients>();
        var mockGroupProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockGroupProxy.Object);
        _hub.Setup(h => h.Clients).Returns(mockClients.Object);

        var controller = new VerificationController(
            _sessionStore.Object,
            _verifiedIdClient.Object,
            _notifications.Object,
            _hub.Object,
            config,
            NullLogger<VerificationController>.Instance);

        // Simulate an authenticated agent with "oid" and "name" claims.
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("oid", agentOid),
                    new Claim("name", "Test Agent")
                }, "Bearer"))
            }
        };
        controller.ControllerContext.HttpContext.Request.Scheme = "https";
        controller.ControllerContext.HttpContext.Request.Host = new HostString("api.test");

        return controller;
    }

    // ── Generate: input validation ─────────────────────────────────────────────

    [Fact]
    public async Task Generate_ReturnsBadRequest_WhenCallerEmailIsNull()
    {
        var result = await CreateController().Generate(new GenerateRequest(
            CallerEntraId: "entra-id",
            CallerEmail: null,
            CallerDisplayName: "Test Caller",
            TicketId: "INC-001",
            Note: null,
            DeliveryChannel: "email"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Generate_ReturnsBadRequest_WhenCallerEntraIdIsNull()
    {
        var result = await CreateController().Generate(new GenerateRequest(
            CallerEntraId: null,
            CallerEmail: "caller@test.com",
            CallerDisplayName: "Test Caller",
            TicketId: "INC-001",
            Note: null,
            DeliveryChannel: "email"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Generate_ReturnsBadRequest_WhenDeliveryChannelIsNull()
    {
        var result = await CreateController().Generate(new GenerateRequest(
            CallerEntraId: "entra-id",
            CallerEmail: "caller@test.com",
            CallerDisplayName: "Test Caller",
            TicketId: null,
            Note: null,
            DeliveryChannel: null));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// Verifies that notes longer than 500 characters are rejected.
    /// The 500-char limit is enforced server-side; it must not be possible to
    /// store an arbitrarily long note by bypassing the UI.
    /// </summary>
    [Fact]
    public async Task Generate_ReturnsBadRequest_WhenNoteExceeds500Characters()
    {
        var result = await CreateController().Generate(new GenerateRequest(
            CallerEntraId: "entra-id",
            CallerEmail: "caller@test.com",
            CallerDisplayName: "Test Caller",
            TicketId: "INC-001",
            Note: new string('A', 501),
            DeliveryChannel: "email"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Generate: rate limiting ────────────────────────────────────────────────

    /// <summary>
    /// Verifies that an agent who already has <see cref="Constants.MaxPendingSessionsPerAgent"/>
    /// pending sessions cannot generate another. This prevents a single agent from
    /// flooding the system with unresolved sessions.
    /// </summary>
    [Fact]
    public async Task Generate_ReturnsBadRequest_WhenAgentExceedsMaxPendingSessions()
    {
        _sessionStore.Setup(s => s.CountPendingByAgentAsync("agent-oid-123"))
            .ReturnsAsync(Constants.MaxPendingSessionsPerAgent);

        var result = await CreateController().Generate(new GenerateRequest(
            CallerEntraId: "entra-id",
            CallerEmail: "caller@test.com",
            CallerDisplayName: "Test Caller",
            TicketId: "INC-001",
            Note: null,
            DeliveryChannel: "email"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Generate: happy path ───────────────────────────────────────────────────

    private void SetupGenerateHappyPath()
    {
        _sessionStore.Setup(s => s.CountPendingByAgentAsync("agent-oid-123")).ReturnsAsync(0);
        _sessionStore.Setup(s => s.CreateAsync(It.IsAny<VerificationSession>()))
            .ReturnsAsync((VerificationSession s) => s);
        _notifications.Setup(n => n.SendCodeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
    }

    private static GenerateRequest ValidGenerateRequest() => new(
        CallerEntraId: "entra-id",
        CallerEmail: "caller@test.com",
        CallerDisplayName: "Test Caller",
        TicketId: "INC-001",
        Note: null,
        DeliveryChannel: "email");

    [Fact]
    public async Task Generate_ReturnsOk_WithSessionIdAndDisplayCode_OnValidRequest()
    {
        SetupGenerateHappyPath();
        var result = await CreateController().Generate(ValidGenerateRequest());

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    /// <summary>
    /// Verifies that <see cref="ISessionStore.CreateAsync"/> is called exactly once,
    /// confirming the session is actually persisted.
    /// </summary>
    [Fact]
    public async Task Generate_CallsSessionStore_ExactlyOnce_OnValidRequest()
    {
        SetupGenerateHappyPath();
        await CreateController().Generate(ValidGenerateRequest());

        _sessionStore.Verify(s => s.CreateAsync(It.IsAny<VerificationSession>()), Times.Once());
    }

    /// <summary>
    /// Verifies that the notification service is called exactly once so the caller
    /// receives their code via the requested channel.
    /// </summary>
    [Fact]
    public async Task Generate_CallsNotificationService_ExactlyOnce_OnValidRequest()
    {
        SetupGenerateHappyPath();
        await CreateController().Generate(ValidGenerateRequest());

        _notifications.Verify(n => n.SendCodeAsync(
            "caller@test.com", It.IsAny<string>(), It.IsAny<DateTime>(), "email"),
            Times.Once());
    }

    /// <summary>
    /// Security: Verifies that the plaintext code returned to the agent is NOT the
    /// HMAC hash. The hash is 44 base64 characters; the plaintext is 8 charset chars.
    /// This test would catch a regression where the hash is accidentally returned.
    /// </summary>
    [Fact]
    public async Task Generate_ReturnedCodeIsPlaintext_NotHash()
    {
        SetupGenerateHappyPath();
        var result = await CreateController().Generate(ValidGenerateRequest());

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        var doc = System.Text.Json.JsonDocument.Parse(json);

        var code = doc.RootElement.GetProperty("code").GetString()!;

        // Plaintext codes are exactly Constants.CodeLength chars from the charset.
        // A base64-encoded HMAC-SHA256 hash would be 44 characters.
        Assert.Equal(Constants.CodeLength, code.Length);
        Assert.All(code, ch => Assert.Contains(ch, Constants.CodeCharset));
    }

    // ── Initiate: input validation ─────────────────────────────────────────────

    [Fact]
    public async Task Initiate_ReturnsBadRequest_WhenEmailIsEmpty()
    {
        var result = await CreateController().Initiate(new InitiateRequest("", "ABCD1234"));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Initiate_ReturnsBadRequest_WhenCodeIsEmpty()
    {
        var result = await CreateController().Initiate(new InitiateRequest("caller@test.com", ""));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Initiate: session state validation ───────────────────────────────────

    [Fact]
    public async Task Initiate_ReturnsBadRequest_WhenNoSessionFound()
    {
        _sessionStore.Setup(s => s.GetByCodeHashAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((VerificationSession?)null);

        var result = await CreateController().Initiate(new InitiateRequest("caller@test.com", "ABCDEFGH"));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// Verifies that an expired session is rejected. The expiry is always checked
    /// server-side — the client cannot extend a session by manipulating timestamps.
    /// </summary>
    [Fact]
    public async Task Initiate_ReturnsBadRequest_WhenSessionIsExpired()
    {
        _sessionStore.Setup(s => s.GetByCodeHashAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new VerificationSession
            {
                Status = SessionStatus.Pending,
                ExpiresAt = DateTime.UtcNow.AddMinutes(-5) // already expired
            });

        var result = await CreateController().Initiate(new InitiateRequest("caller@test.com", "ABCDEFGH"));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// Verifies that a session that is no longer "pending" (e.g., already verified
    /// or failed) cannot be re-initiated. This prevents replay attacks.
    /// </summary>
    [Fact]
    public async Task Initiate_ReturnsBadRequest_WhenSessionStatusIsNotPending()
    {
        _sessionStore.Setup(s => s.GetByCodeHashAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new VerificationSession
            {
                Status = SessionStatus.Verified,  // already verified
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });

        var result = await CreateController().Initiate(new InitiateRequest("caller@test.com", "ABCDEFGH"));
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Initiate: happy path ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a valid initiate request calls the Verified ID client and
    /// persists the returned requestId on the session.
    /// </summary>
    [Fact]
    public async Task Initiate_ReturnsOk_AndSetsRequestId_OnValidRequest()
    {
        var session = new VerificationSession
        {
            SessionId = "test-session-id",
            Status = SessionStatus.Pending,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            FailedAttempts = 0
        };
        _sessionStore.Setup(s => s.GetByCodeHashAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(session);
        _sessionStore.Setup(s => s.UpdateAsync(It.IsAny<VerificationSession>()))
            .Returns(Task.CompletedTask);
        _verifiedIdClient.Setup(v => v.CreatePresentationRequestAsync(
            It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new PresentationRequestResult
            {
                RequestId = "req-abc123",
                QrCodeUri = "data://qr",
                DeepLink = "openid-vc://deep"
            });

        var result = await CreateController().Initiate(new InitiateRequest("caller@test.com", "ABCDEFGH"));

        Assert.IsType<OkObjectResult>(result);

        // The requestId must be written back to the session so the callback can correlate.
        _sessionStore.Verify(s =>
            s.UpdateAsync(It.Is<VerificationSession>(sess => sess.RequestId == "req-abc123")),
            Times.Once());
    }

    // ── Initiate: brute-force protection ─────────────────────────────────────

    /// <summary>
    /// Verifies that after <see cref="Constants.MaxFailedAttempts"/> failed attempts
    /// the session is locked (status = "failed") and further attempts are rejected.
    /// This prevents brute-force guessing of the 8-character code.
    /// </summary>
    [Fact]
    public async Task Initiate_ReturnsBadRequest_WhenMaxFailedAttemptsExceeded()
    {
        var session = new VerificationSession
        {
            Status = SessionStatus.Pending,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            FailedAttempts = Constants.MaxFailedAttempts // one more increment exceeds the limit
        };
        _sessionStore.Setup(s => s.GetByCodeHashAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(session);
        _sessionStore.Setup(s => s.UpdateAsync(It.IsAny<VerificationSession>()))
            .Returns(Task.CompletedTask);

        var result = await CreateController().Initiate(new InitiateRequest("caller@test.com", "ABCDEFGH"));

        Assert.IsType<BadRequestObjectResult>(result);

        // Session must be locked so subsequent calls fail even with the correct code.
        _sessionStore.Verify(s =>
            s.UpdateAsync(It.Is<VerificationSession>(sess => sess.Status == SessionStatus.Failed)),
            Times.Once());
    }

    // ── Status (agent) ────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_ReturnsNotFound_WhenSessionMissing()
    {
        _sessionStore.Setup(s => s.GetAsync("missing-id")).ReturnsAsync((VerificationSession?)null);

        var result = await CreateController().Status("missing-id");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Status_ReturnsOk_WithStatusAndClaims_WhenSessionFound()
    {
        _sessionStore.Setup(s => s.GetAsync("valid-id"))
            .ReturnsAsync(new VerificationSession
            {
                Status = SessionStatus.Verified,
                VerifiedAt = DateTime.UtcNow,
                VerifiedClaims = "{\"displayName\":\"Jane Doe\"}"
            });

        var result = await CreateController().Status("valid-id");

        Assert.IsType<OkObjectResult>(result);
    }

    // ── PublicStatus ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PublicStatus_ReturnsNotFound_WhenSessionMissing()
    {
        _sessionStore.Setup(s => s.GetAsync(It.IsAny<string>())).ReturnsAsync((VerificationSession?)null);

        var result = await CreateController().PublicStatus("nonexistent-id");

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// Security: Verifies that PublicStatus returns ONLY the status string —
    /// not verified claims or any PII. The public endpoint is accessible without
    /// authentication and is polled by the caller's browser.
    /// </summary>
    [Fact]
    public async Task PublicStatus_ReturnsStatusOnly_WithoutVerifiedClaims()
    {
        _sessionStore.Setup(s => s.GetAsync("valid-id"))
            .ReturnsAsync(new VerificationSession
            {
                Status = SessionStatus.Verified,
                VerifiedClaims = "{\"displayName\":\"Jane Doe\",\"employeeId\":\"EMP-999\"}"
            });

        var result = await CreateController().PublicStatus("valid-id");

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);

        // Status must be present
        Assert.Contains("verified", json);
        // PII must NOT leak through the public endpoint
        Assert.DoesNotContain("verifiedClaims", json);
        Assert.DoesNotContain("Jane Doe", json);
        Assert.DoesNotContain("EMP-999", json);
    }
}
