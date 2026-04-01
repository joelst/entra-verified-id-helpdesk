using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;
using Moq;
using AgentPortalVerificationController = VerifiedIdHelpdesk.AgentPortal.Controllers.VerificationController;
using ErrorViewModel = VerifiedIdHelpdesk.AgentPortal.Models.ErrorViewModel;
using HistoryViewModel = VerifiedIdHelpdesk.AgentPortal.Controllers.HistoryViewModel;
using PendingViewModel = VerifiedIdHelpdesk.AgentPortal.Controllers.PendingViewModel;
using ResultViewModel = VerifiedIdHelpdesk.AgentPortal.Controllers.ResultViewModel;

namespace VerifiedIdHelpdesk.UnitTests;

/// <summary>
/// Unit tests for the AgentPortal verification controller.
///
/// Security significance: the helpdesk-facing Result screen must use an
/// authorized agent-only endpoint to retrieve verified claims. It must not rely
/// on the public status endpoint, which intentionally omits PII.
/// </summary>
public class AgentPortalVerificationControllerTests
{
  [Fact]
  public async Task Result_UsesAuthorizedStatusEndpoint_AndBuildsVerifiedIdentityViewModel()
  {
    var handler = new RecordingHttpMessageHandler(request =>
    {
      var payload =
              """
                {
                  "status": "verified",
                  "verifiedClaims": "{\"displayName\":\"Jane Doe\",\"employeeId\":\"EMP-123\",\"department\":\"Finance\"}",
                  "verifiedAt": "2026-03-31T12:34:56Z"
                }
                """;

      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(payload)
      };
    });

    var controller = CreateController(handler);

    var result = await controller.Result("session-123");

    var view = Assert.IsType<ViewResult>(result);
    var model = Assert.IsType<ResultViewModel>(view.Model);
    Assert.Equal("Jane Doe", model.CallerName);
    Assert.Equal("EMP-123", model.EmployeeId);
    Assert.Equal("Finance", model.Department);

    Assert.NotNull(handler.LastRequest);
    Assert.Equal("/api/verification/status/session-123", handler.LastRequest!.RequestUri!.PathAndQuery);
    Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
    Assert.Equal("test-access-token", handler.LastRequest.Headers.Authorization?.Parameter);
  }

  [Fact]
  public async Task Result_FallsBackToRouteId_WhenSessionIdComesFromConventionalRouting()
  {
    var handler = new RecordingHttpMessageHandler(request =>
    {
      var payload =
              """
                {
                  "status": "verified",
                  "verifiedClaims": "{\"displayName\":\"Jane Doe\",\"employeeId\":\"EMP-123\",\"department\":\"Finance\"}",
                  "verifiedAt": "2026-03-31T12:34:56Z"
                }
                """;

      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(payload)
      };
    });

    var controller = CreateController(handler);
    controller.ControllerContext.RouteData = new RouteData();
    controller.ControllerContext.RouteData.Values["id"] = "session-from-route";

    var result = await controller.Result(null);

    var view = Assert.IsType<ViewResult>(result);
    _ = Assert.IsType<ResultViewModel>(view.Model);
    Assert.NotNull(handler.LastRequest);
    Assert.Equal("/api/verification/status/session-from-route", handler.LastRequest!.RequestUri!.PathAndQuery);
  }

  [Fact]
  public async Task Result_ReturnsActionableError_WhenApiReturnsUnauthorized()
  {
    var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
    var controller = CreateController(handler);

    var result = await controller.Result("session-401");

    var view = Assert.IsType<ViewResult>(result);
    Assert.Equal("Error", view.ViewName);
    var model = Assert.IsType<ErrorViewModel>(view.Model);
    Assert.Contains("sign in", model.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task History_ShowsPermissionError_WhenApiReturnsForbidden()
  {
    var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
    var controller = CreateController(handler);

    var result = await controller.History();

    var view = Assert.IsType<ViewResult>(result);
    var model = Assert.IsType<HistoryViewModel>(view.Model);
    Assert.Empty(model.Sessions);
    Assert.Contains("permission", model.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Create_ShowsFriendlyError_WhenApiIsUnavailable()
  {
    var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
    {
      Content = new StringContent("backend offline")
    });

    var controller = CreateController(handler);
    var model = new VerifiedIdHelpdesk.AgentPortal.Controllers.CreateVerificationViewModel
    {
      CallerEntraId = "caller-123",
      CallerEmail = "caller@contoso.com",
      CallerDisplayName = "Caller Example",
      DeliveryChannel = "email"
    };

    var result = await controller.Create(model);

    var view = Assert.IsType<ViewResult>(result);
    Assert.Same(model, view.Model);
    var errorMessage = Assert.Single(controller.ModelState[string.Empty]!.Errors).ErrorMessage;
    Assert.Contains("temporarily unavailable", errorMessage, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Pending_UsesConfiguredVerifyPortalUrl_ForCallerInstructions()
  {
    var controller = CreateController(new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

    var result = controller.Pending(
        "session-123",
        "ABCD-EFGH",
        "Caller Example",
        "INC-123",
        "verbal",
        DateTime.UtcNow.AddMinutes(10).ToString("O"));

    var view = Assert.IsType<ViewResult>(result);
    var model = Assert.IsType<PendingViewModel>(view.Model);
    Assert.Equal("https://verify.contoso.com", model.VerifyPortalUrl);
  }

  private static AgentPortalVerificationController CreateController(RecordingHttpMessageHandler handler)
  {
    var httpClient = new HttpClient(handler)
    {
      BaseAddress = new Uri("https://api.test")
    };

    var httpClientFactory = new Mock<IHttpClientFactory>();
    httpClientFactory.Setup(f => f.CreateClient("ApiClient")).Returns(httpClient);

    var tokenAcquisition = new Mock<ITokenAcquisition>(MockBehavior.Strict);
    tokenAcquisition
        .Setup(t => t.GetAccessTokenForUserAsync(
            It.IsAny<IEnumerable<string>>(),
            null,
            null,
            null,
            null))
        .ReturnsAsync("test-access-token");
    tokenAcquisition
        .Setup(t => t.GetAccessTokenForUserAsync(
            It.IsAny<IEnumerable<string>>(),
            null,
            null,
            null,
            null,
            null))
        .ReturnsAsync("test-access-token");

    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
          ["Api:Scopes:0"] = "api://test/access_as_agent",
          ["VerifyPortal:BaseUrl"] = "https://verify.contoso.com"
        })
        .Build();

    return new AgentPortalVerificationController(
        httpClientFactory.Object,
        tokenAcquisition.Object,
        config,
        NullLogger<AgentPortalVerificationController>.Instance)
    {
      ControllerContext = new ControllerContext
      {
        HttpContext = new DefaultHttpContext
        {
          User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("oid", "agent-oid-123"),
                    new Claim("name", "Test Agent")
                ], "Cookies"))
        }
      }
    };
  }

  private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
  {
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      LastRequest = request;
      return Task.FromResult(_responder(request));
    }
  }
}
