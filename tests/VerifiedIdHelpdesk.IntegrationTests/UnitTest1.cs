namespace VerifiedIdHelpdesk.IntegrationTests;

/// <summary>
/// Integration test placeholders.
///
/// Full integration tests require Azure resources (Table Storage, Key Vault, Entra tenant).
/// Run these against a dedicated dev environment with test credentials configured.
///
/// To enable: set environment variables before running:
///   AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET (or use managed identity)
///   and point KeyVault:Uri to a dev vault with test secrets.
/// </summary>
public class ApiSmokeTests
{
    [Fact(Skip = "Requires live Azure environment — run manually against dev deployment")]
    public void ApiEndpoints_AreReachable()
    {
        // Placeholder: use HttpClient to hit /health or /api/verification/initiate
        // with an invalid code and assert 400 is returned.
        Assert.True(true);
    }

    [Fact(Skip = "Requires live Azure environment — run manually against dev deployment")]
    public void VerifyPortal_Returns200_ForIndexPage()
    {
        // Placeholder: WebApplicationFactory<VerifyPortalProgram> with mocked HttpClient
        Assert.True(true);
    }
}
