using System.Globalization;
using System.Reflection;
using VerifiedIdHelpdesk.Notifications;

namespace VerifiedIdHelpdesk.UnitTests;

/// <summary>
/// Regression tests for notification formatting.
///
/// Security/UX significance: Teams notifications must contain visible caller
/// instructions and not rely on markdown that may render as an empty or broken
/// message body in Teams chat.
/// </summary>
public class GraphNotificationServiceTests
{
  [Fact]
  public void NormalizeChannel_Teams_FallsBackToEmail()
  {
    var method = typeof(GraphNotificationService).GetMethod(
        "NormalizeChannel",
        BindingFlags.NonPublic | BindingFlags.Static);

    Assert.NotNull(method);

    var effectiveChannel = Assert.IsType<string>(method!.Invoke(null, ["teams"]));

    Assert.Equal("email", effectiveChannel);
  }

  [Fact]
  public void BuildTeamsMessageHtml_ReturnsHtmlWithPortalUrlAndCode()
  {
    var expiresAt = new DateTime(2026, 03, 31, 23, 45, 00, DateTimeKind.Utc);
    var method = typeof(GraphNotificationService).GetMethod(
        "BuildTeamsMessageHtml",
        BindingFlags.NonPublic | BindingFlags.Static);

    Assert.NotNull(method);

    var html = Assert.IsType<string>(method!.Invoke(null, ["ABCD-EFGH", "https://verify.contoso.com", expiresAt]));

    Assert.Contains("<p>", html, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("ABCD-EFGH", html, StringComparison.Ordinal);
    Assert.Contains("https://verify.contoso.com", html, StringComparison.Ordinal);
    Assert.Contains(expiresAt.ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture), html, StringComparison.Ordinal);
    Assert.DoesNotContain("**", html, StringComparison.Ordinal);
  }
}
