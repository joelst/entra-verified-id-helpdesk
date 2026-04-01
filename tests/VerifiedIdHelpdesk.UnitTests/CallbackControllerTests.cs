using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;
using VerifiedIdHelpdesk.Api.Controllers;
using VerifiedIdHelpdesk.Api.Hubs;
using VerifiedIdHelpdesk.Core.Interfaces;
using VerifiedIdHelpdesk.Core.Models;

namespace VerifiedIdHelpdesk.UnitTests;

/// <summary>
/// Unit tests for <see cref="CallbackController"/>.
///
/// The controller is instantiated directly (no HTTP middleware) with mocked
/// dependencies so each test exercises only the controller logic.
///
/// Security significance: The callback endpoint is invoked by Entra Verified ID.
/// It must reject payloads without a session correlator (state), silently accept
/// unknown or already-processed sessions (preventing retry storms), and correctly
/// transition sessions to "verified" only on presentation_verified events.
/// </summary>
public class CallbackControllerTests
{
    private readonly Mock<ISessionStore> _sessionStore = new();
    private readonly Mock<IHubContext<VerificationHub>> _hub = new();
    private readonly Mock<IClientProxy> _groupProxy = new();

    /// <summary>
    /// Creates a controller instance wired with the shared mocks.
    /// </summary>
    private CallbackController CreateController()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VerifiedId:TenantId"] = "00000000-0000-0000-0000-000000000000",
                ["AzureAd:ClientId"] = "11111111-1111-1111-1111-111111111111"
            })
            .Build();

        var mockClients = new Mock<IHubClients>();
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxy.Object);
        _hub.Setup(h => h.Clients).Returns(mockClients.Object);

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns("Testing");

        return new CallbackController(
            _sessionStore.Object,
            _hub.Object,
            config,
            NullLogger<CallbackController>.Instance,
            env.Object);
    }

    /// <summary>
    /// Builds a <see cref="JsonElement"/> representing a callback payload.
    /// </summary>
    private static JsonElement BuildCallbackBody(
        string? state = null,
        string? requestStatus = null,
        string? requestId = null,
        Dictionary<string, string>? claims = null)
    {
        var obj = new Dictionary<string, object?>();

        if (state != null)
            obj["state"] = state;
        if (requestStatus != null)
            obj["requestStatus"] = requestStatus;
        if (requestId != null)
            obj["requestId"] = requestId;

        if (claims != null)
        {
            obj["verifiedCredentialsData"] = new[]
            {
                new Dictionary<string, object> { ["claims"] = claims }
            };
        }

        var json = JsonSerializer.Serialize(obj);
        return JsonDocument.Parse(json).RootElement;
    }

    // ── Missing state ──────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a callback without the "state" field is rejected with 400.
    /// The state parameter correlates the callback to a session; without it the
    /// controller cannot determine which session to update.
    /// </summary>
    [Fact]
    public async Task Callback_ReturnsBadRequest_WhenStateMissing()
    {
        var body = BuildCallbackBody(state: null, requestStatus: "presentation_verified");

        var result = await CreateController().Callback(body);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// Verifies that an empty-string state is treated the same as a missing state.
    /// </summary>
    [Fact]
    public async Task Callback_ReturnsBadRequest_WhenStateIsEmpty()
    {
        var body = BuildCallbackBody(state: "", requestStatus: "presentation_verified");

        var result = await CreateController().Callback(body);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Unknown session ────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a callback referencing an unknown session returns 200.
    /// Returning a non-success status would cause Entra Verified ID to retry,
    /// creating unnecessary load. The session may have been cleaned up already.
    /// </summary>
    [Fact]
    public async Task Callback_ReturnsOk_WhenSessionNotFound()
    {
        _sessionStore.Setup(s => s.GetAsync("unknown-session-id"))
            .ReturnsAsync((VerificationSession?)null);

        var body = BuildCallbackBody(state: "unknown-session-id", requestStatus: "presentation_verified");

        var result = await CreateController().Callback(body);

        Assert.IsType<OkResult>(result);
    }

    /// <summary>
    /// Verifies that the session store is NOT updated when the session is unknown.
    /// </summary>
    [Fact]
    public async Task Callback_DoesNotUpdateStore_WhenSessionNotFound()
    {
        _sessionStore.Setup(s => s.GetAsync("unknown-session-id"))
            .ReturnsAsync((VerificationSession?)null);

        var body = BuildCallbackBody(state: "unknown-session-id", requestStatus: "presentation_verified");

        await CreateController().Callback(body);

        _sessionStore.Verify(s => s.UpdateAsync(It.IsAny<VerificationSession>()), Times.Never());
    }

    // ── Idempotent callbacks ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a duplicate callback for an already-verified session returns 200
    /// without updating the session again. Entra Verified ID may send duplicate
    /// webhooks; double-processing could overwrite VerifiedAt or send duplicate
    /// SignalR notifications.
    /// </summary>
    [Fact]
    public async Task Callback_ReturnsOk_WhenSessionAlreadyVerified()
    {
        _sessionStore.Setup(s => s.GetAsync("verified-session-id"))
            .ReturnsAsync(new VerificationSession
            {
                SessionId = "verified-session-id",
                Status = "verified",
                VerifiedAt = DateTime.UtcNow.AddMinutes(-1)
            });

        var body = BuildCallbackBody(state: "verified-session-id", requestStatus: "presentation_verified");

        var result = await CreateController().Callback(body);

        Assert.IsType<OkResult>(result);
    }

    /// <summary>
    /// Verifies that no store update occurs for an already-verified session,
    /// ensuring true idempotency.
    /// </summary>
    [Fact]
    public async Task Callback_DoesNotUpdateStore_WhenSessionAlreadyVerified()
    {
        _sessionStore.Setup(s => s.GetAsync("verified-session-id"))
            .ReturnsAsync(new VerificationSession
            {
                SessionId = "verified-session-id",
                Status = "verified",
                VerifiedAt = DateTime.UtcNow.AddMinutes(-1)
            });

        var body = BuildCallbackBody(state: "verified-session-id", requestStatus: "presentation_verified");

        await CreateController().Callback(body);

        _sessionStore.Verify(s => s.UpdateAsync(It.IsAny<VerificationSession>()), Times.Never());
    }

    /// <summary>
    /// Verifies idempotency for failed sessions as well — a callback arriving
    /// after a session has already failed must not revert its status.
    /// </summary>
    [Fact]
    public async Task Callback_ReturnsOk_WhenSessionAlreadyFailed()
    {
        _sessionStore.Setup(s => s.GetAsync("failed-session-id"))
            .ReturnsAsync(new VerificationSession
            {
                SessionId = "failed-session-id",
                Status = "failed"
            });

        var body = BuildCallbackBody(state: "failed-session-id", requestStatus: "presentation_verified");

        var result = await CreateController().Callback(body);

        Assert.IsType<OkResult>(result);
        _sessionStore.Verify(s => s.UpdateAsync(It.IsAny<VerificationSession>()), Times.Never());
    }

    // ── presentation_verified happy path ───────────────────────────────────────

    /// <summary>
    /// Verifies that a presentation_verified callback transitions the session
    /// status to "verified" and persists the update.
    /// </summary>
    [Fact]
    public async Task Callback_UpdatesSessionToVerified_WhenPresentationVerified()
    {
        var session = new VerificationSession
        {
            SessionId = "pending-session-id",
            Status = "pending",
            CallerDisplayName = "Test Caller"
        };
        _sessionStore.Setup(s => s.GetAsync("pending-session-id")).ReturnsAsync(session);
        _sessionStore.Setup(s => s.UpdateAsync(It.IsAny<VerificationSession>())).Returns(Task.CompletedTask);

        var claims = new Dictionary<string, string>
        {
            ["displayName"] = "Jane Doe",
            ["employeeId"] = "EMP-123",
            ["department"] = "Engineering"
        };
        var body = BuildCallbackBody(
            state: "pending-session-id",
            requestStatus: "presentation_verified",
            claims: claims);

        var result = await CreateController().Callback(body);

        Assert.IsType<OkResult>(result);
        _sessionStore.Verify(s =>
            s.UpdateAsync(It.Is<VerificationSession>(sess =>
                sess.Status == "verified" && sess.VerifiedAt != null)),
            Times.Once());
    }

    /// <summary>
    /// Verifies that verified claims from the presentation are persisted on the session.
    /// </summary>
    [Fact]
    public async Task Callback_PersistsVerifiedClaims_WhenPresentationVerified()
    {
        var session = new VerificationSession
        {
            SessionId = "pending-session-id",
            Status = "pending",
            CallerDisplayName = "Test Caller"
        };
        _sessionStore.Setup(s => s.GetAsync("pending-session-id")).ReturnsAsync(session);
        _sessionStore.Setup(s => s.UpdateAsync(It.IsAny<VerificationSession>())).Returns(Task.CompletedTask);

        var claims = new Dictionary<string, string>
        {
            ["displayName"] = "Jane Doe",
            ["employeeId"] = "EMP-123"
        };
        var body = BuildCallbackBody(
            state: "pending-session-id",
            requestStatus: "presentation_verified",
            claims: claims);

        await CreateController().Callback(body);

        _sessionStore.Verify(s =>
            s.UpdateAsync(It.Is<VerificationSession>(sess =>
                sess.VerifiedClaims != null
                && sess.VerifiedClaims.Contains("Jane Doe")
                && sess.VerifiedClaims.Contains("EMP-123"))),
            Times.Once());
    }

    /// <summary>
    /// Verifies that a SignalR VerificationComplete message is sent to the session
    /// group so the agent portal receives real-time notification.
    /// </summary>
    [Fact]
    public async Task Callback_SendsSignalRNotification_WhenPresentationVerified()
    {
        var session = new VerificationSession
        {
            SessionId = "pending-session-id",
            Status = "pending",
            CallerDisplayName = "Test Caller"
        };
        _sessionStore.Setup(s => s.GetAsync("pending-session-id")).ReturnsAsync(session);
        _sessionStore.Setup(s => s.UpdateAsync(It.IsAny<VerificationSession>())).Returns(Task.CompletedTask);

        var body = BuildCallbackBody(
            state: "pending-session-id",
            requestStatus: "presentation_verified",
            claims: new Dictionary<string, string> { ["displayName"] = "Jane Doe" });

        await CreateController().Callback(body);

        _groupProxy.Verify(p =>
            p.SendCoreAsync("VerificationComplete", It.IsAny<object?[]>(), default),
            Times.Once());
    }

    // ── presentation_error ─────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a presentation_error callback transitions the session to "failed".
    /// </summary>
    [Fact]
    public async Task Callback_UpdatesSessionToFailed_WhenPresentationError()
    {
        var session = new VerificationSession
        {
            SessionId = "pending-session-id",
            Status = "pending"
        };
        _sessionStore.Setup(s => s.GetAsync("pending-session-id")).ReturnsAsync(session);
        _sessionStore.Setup(s => s.UpdateAsync(It.IsAny<VerificationSession>())).Returns(Task.CompletedTask);

        var body = BuildCallbackBody(
            state: "pending-session-id",
            requestStatus: "presentation_error");

        var result = await CreateController().Callback(body);

        Assert.IsType<OkResult>(result);
        _sessionStore.Verify(s =>
            s.UpdateAsync(It.Is<VerificationSession>(sess => sess.Status == "failed")),
            Times.Once());
    }

    /// <summary>
    /// Verifies that a SignalR VerificationFailed message is sent on presentation error.
    /// </summary>
    [Fact]
    public async Task Callback_SendsSignalRFailureNotification_WhenPresentationError()
    {
        var session = new VerificationSession
        {
            SessionId = "pending-session-id",
            Status = "pending"
        };
        _sessionStore.Setup(s => s.GetAsync("pending-session-id")).ReturnsAsync(session);
        _sessionStore.Setup(s => s.UpdateAsync(It.IsAny<VerificationSession>())).Returns(Task.CompletedTask);

        var body = BuildCallbackBody(
            state: "pending-session-id",
            requestStatus: "presentation_error");

        await CreateController().Callback(body);

        _groupProxy.Verify(p =>
            p.SendCoreAsync("VerificationFailed", It.IsAny<object?[]>(), default),
            Times.Once());
    }
}
