extern alias VerifyPortalApp;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using VerifyPortalEntryPoint = VerifyPortalApp::VerifiedIdHelpdesk.VerifyPortal.Pages.IndexModel;

namespace VerifiedIdHelpdesk.IntegrationTests;

/// <summary>
/// Lightweight integration tests that exercise the public Verify Portal pages
/// through the full Razor Pages pipeline. These tests protect against runtime
/// page-activation regressions that unit tests would not catch.
/// </summary>
public sealed class VerifyPortalWebApplicationFactory : WebApplicationFactory<VerifyPortalEntryPoint>
{
  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseEnvironment("Testing");

    builder.ConfigureAppConfiguration((_, config) =>
    {
      config.Sources.Clear();
      config.AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["Api:BaseUrl"] = "http://localhost:5001",
        ["VerifiedId:EnrollmentUrl"] = "https://contoso.example/enroll",
        ["Logging:LogLevel:Default"] = "Warning",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Error",
        ["ApplicationInsights:ConnectionString"] =
                  "InstrumentationKey=00000000-0000-0000-0000-000000000000;" +
                  "IngestionEndpoint=https://test.applicationinsights.azure.com/;",
      });
    });
  }
}

public class VerifyPortalPageRenderTests : IClassFixture<VerifyPortalWebApplicationFactory>
{
  private readonly HttpClient _client;

  public VerifyPortalPageRenderTests(VerifyPortalWebApplicationFactory factory)
  {
    _client = factory.CreateClient();
  }

  /// <summary>
  /// The public landing page must render successfully because callers rely on it
  /// to start the verification flow without authentication.
  /// </summary>
  [Fact]
  public async Task IndexPage_RendersSuccessfully()
  {
    var response = await _client.GetAsync("/");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var html = await response.Content.ReadAsStringAsync();
    Assert.Contains("Verify Your Identity", html);
    Assert.Contains("Create your Verified ID", html);
  }

  /// <summary>
  /// The presentation page must render with a session id so the polling script
  /// and Authenticator deeplink workflow can continue after the caller is redirected.
  /// </summary>
  [Fact]
  public async Task PresentPage_RendersSuccessfully_WithSessionId()
  {
    var response = await _client.GetAsync("/Present?sessionId=test-session");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var html = await response.Content.ReadAsStringAsync();
    Assert.Contains("Present Your Credential", html);
    Assert.Contains("test-session", html);
    Assert.Contains("Create it here", html);
  }
}
