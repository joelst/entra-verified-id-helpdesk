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

public class VerificationControllerTests
{
    private readonly Mock<ISessionStore> _sessionStore = new();
    private readonly Mock<IVerifiedIdClient> _verifiedIdClient = new();
    private readonly Mock<INotificationService> _notifications = new();
    private readonly Mock<IHubContext<VerificationHub>> _hub = new();

    private VerificationController CreateController(string agentOid = "agent-oid-123")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HmacKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["Api:BaseUrl"] = "https://api.test"
            })
            .Build();

        // Mock hub clients
        var mockClients = new Mock<IHubClients>();
        var mockGroupClient = new Mock<IClientProxy>();
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockGroupClient.Object);
        _hub.Setup(h => h.Clients).Returns(mockClients.Object);

        var controller = new VerificationController(
            _sessionStore.Object,
            _verifiedIdClient.Object,
            _notifications.Object,
            _hub.Object,
            config,
            NullLogger<VerificationController>.Instance);

        // Set up authenticated user claims
        var claims = new[]
        {
            new Claim("oid", agentOid),
            new Claim("name", "Test Agent")
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
            }
        };
        controller.ControllerContext.HttpContext.Request.Scheme = "https";
        controller.ControllerContext.HttpContext.Request.Host = new HostString("api.test");

        return controller;
    }

    [Fact]
    public async Task Generate_ReturnsBadRequest_WhenCallerEmailMissing()
    {
        var controller = CreateController();
        var request = new GenerateRequest(
            CallerEntraId: "entra-id",
            CallerEmail: null,
            CallerDisplayName: "Test Caller",
            TicketId: "INC-001",
            Note: null,
            DeliveryChannel: "email");

        var result = await controller.Generate(request);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Generate_ReturnsBadRequest_WhenDeliveryChannelMissing()
    {
        var controller = CreateController();
        var request = new GenerateRequest(
            CallerEntraId: "entra-id",
            CallerEmail: "caller@test.com",
            CallerDisplayName: "Test Caller",
            TicketId: null,
            Note: null,
            DeliveryChannel: null);

        var result = await controller.Generate(request);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Generate_ReturnsBadRequest_WhenAgentExceedsMaxPending()
    {
        _sessionStore.Setup(s => s.CountPendingByAgentAsync("agent-oid-123"))
            .ReturnsAsync(Constants.MaxPendingSessionsPerAgent);

        var controller = CreateController();
        var request = new GenerateRequest(
            CallerEntraId: "entra-id",
            CallerEmail: "caller@test.com",
            CallerDisplayName: "Test Caller",
            TicketId: "INC-001",
            Note: null,
            DeliveryChannel: "email");

        var result = await controller.Generate(request);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Generate_ReturnsOk_WithSessionId_WhenValid()
    {
        _sessionStore.Setup(s => s.CountPendingByAgentAsync("agent-oid-123")).ReturnsAsync(0);
        _sessionStore.Setup(s => s.CreateAsync(It.IsAny<VerificationSession>()))
            .ReturnsAsync((VerificationSession s) => s);
        _notifications.Setup(n => n.SendCodeAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        var controller = CreateController();
        var request = new GenerateRequest(
            CallerEntraId: "entra-id",
            CallerEmail: "caller@test.com",
            CallerDisplayName: "Test Caller",
            TicketId: "INC-001",
            Note: null,
            DeliveryChannel: "email");

        var result = await controller.Generate(request);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task Initiate_ReturnsBadRequest_WhenCodeExpired()
    {
        var expiredSession = new VerificationSession
        {
            Status = "pending",
            ExpiresAt = DateTime.UtcNow.AddMinutes(-5)  // already expired
        };
        _sessionStore.Setup(s => s.GetByCodeHashAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(expiredSession);

        var controller = CreateController();
        var result = await controller.Initiate(new InitiateRequest("user@test.com", "ABCD1234"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Status_ReturnsNotFound_WhenSessionMissing()
    {
        _sessionStore.Setup(s => s.GetAsync("missing-id")).ReturnsAsync((VerificationSession?)null);
        var controller = CreateController();

        var result = await controller.Status("missing-id");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Status_ReturnsOk_WithSessionData()
    {
        var session = new VerificationSession { Status = "verified", VerifiedAt = DateTime.UtcNow };
        _sessionStore.Setup(s => s.GetAsync("valid-id")).ReturnsAsync(session);
        var controller = CreateController();

        var result = await controller.Status("valid-id");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task PublicStatus_ReturnsOnlyStatus_NotClaims()
    {
        var session = new VerificationSession
        {
            Status = "verified",
            VerifiedClaims = "{\"displayName\":\"John Doe\"}"
        };
        _sessionStore.Setup(s => s.GetAsync("valid-id")).ReturnsAsync(session);
        var controller = CreateController();

        var result = await controller.PublicStatus("valid-id");

        var ok = Assert.IsType<OkObjectResult>(result);
        // Serialize to verify only status is present
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("verified", json);
        Assert.DoesNotContain("John Doe", json);
    }
}
