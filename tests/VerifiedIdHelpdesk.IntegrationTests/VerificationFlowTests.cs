using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using VerifiedIdHelpdesk.Core.Interfaces;
using VerifiedIdHelpdesk.Core.Models;

namespace VerifiedIdHelpdesk.IntegrationTests;

// ── In-memory session store ─────────────────────────────────────────────────

/// <summary>
/// Thread-safe in-memory implementation of <see cref="ISessionStore"/> for use
/// in integration tests. Replaces the Azure Table Storage implementation so tests
/// do not require Azure credentials or a network connection.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, VerificationSession> _sessions = new();

    public Task<VerificationSession> CreateAsync(VerificationSession session)
    {
        _sessions[session.SessionId] = session;
        return Task.FromResult(session);
    }

    public Task<VerificationSession?> GetAsync(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task<VerificationSession?> GetByCodeHashAsync(string codeHash, string callerEmail)
    {
        var session = _sessions.Values.FirstOrDefault(s =>
            s.CodeHash == codeHash &&
            s.CallerEmail.Equals(callerEmail, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(session);
    }

    public Task<VerificationSession?> GetMostRecentPendingByCallerEmailAsync(string callerEmail)
    {
        var session = _sessions.Values
            .Where(s => s.CallerEmail.Equals(callerEmail, StringComparison.OrdinalIgnoreCase)
                        && s.Status == SessionStatus.Pending)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefault();

        return Task.FromResult(session);
    }

    public Task<VerificationSession?> GetByRequestIdAsync(string requestId)
    {
        var session = _sessions.Values.FirstOrDefault(s => s.RequestId == requestId);
        return Task.FromResult(session);
    }

    public Task UpdateAsync(VerificationSession session)
    {
        _sessions[session.SessionId] = session;
        return Task.CompletedTask;
    }

    public Task<int> CountPendingByAgentAsync(string agentEntraId)
    {
        var count = _sessions.Values.Count(s =>
            s.AgentEntraId == agentEntraId && s.Status == SessionStatus.Pending);
        return Task.FromResult(count);
    }

    public Task<int> ExpireOldSessionsAsync()
    {
        var now = DateTime.UtcNow;
        var count = 0;
        foreach (var session in _sessions.Values
            .Where(s => s.Status == SessionStatus.Pending && s.ExpiresAt <= now))
        {
            session.Status = SessionStatus.Expired;
            count++;
        }
        return Task.FromResult(count);
    }

    public Task<IReadOnlyList<VerificationSession>> GetByAgentAsync(string agentEntraId, int limit = 50)
    {
        IReadOnlyList<VerificationSession> result = _sessions.Values
            .Where(s => s.AgentEntraId == agentEntraId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<VerificationSession>> GetPendingByAgentAsync(string agentEntraId)
    {
        IReadOnlyList<VerificationSession> result = _sessions.Values
            .Where(s => s.AgentEntraId == agentEntraId && s.Status == SessionStatus.Pending)
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }
}

// ── Test factory ─────────────────────────────────────────────────────────────

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> for the API project.
///
/// Replaces all Azure-dependent services with test doubles and provides
/// in-memory configuration so the test server starts without any Azure
/// credentials or network connectivity.
/// </summary>
public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>Exposed so tests can pre-seed sessions or assert state.</summary>
    public readonly InMemorySessionStore Sessions = new();

    private readonly Mock<IVerifiedIdClient> _verifiedIdMock = new();
    private readonly Mock<INotificationService> _notificationMock = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set the environment to "Testing" — Program.cs skips Key Vault in this environment.
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // Replace all configuration sources with a minimal, self-contained test config.
            // KeyVault:Uri is intentionally absent — Program.cs guards against it being empty.
            config.Sources.Clear();
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAd:TenantId"] = "test-tenant-id",
                ["AzureAd:ClientId"] = "test-client-id",
                ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                ["AzureAd:Audience"] = "test-client-id",
                ["HmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Api:BaseUrl"] = "http://localhost",
                ["AuthorizationGroups:HelpDeskAgents"] = "test-group-id",
                ["VerifiedId:TenantId"] = "test-tenant-id",
                ["VerifiedId:DidAuthority"] = "did:web:test",
                ["VerifiedId:CredentialType"] = "TestCredential",
                ["VerifiedId:RequestServiceBaseUrl"] = "https://test.verifiedid.invalid/",
                ["Storage:AccountUri"] = "https://test.table.core.windows.net/",
                ["AgentPortal:BaseUrl"] = "http://localhost:5001",
                ["VerifyPortal:BaseUrl"] = "http://localhost:5002",
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Error",
                // Provide a fake AI connection string so AddApplicationInsightsTelemetry()
                // initialises without throwing. No real telemetry is sent in tests.
                ["ApplicationInsights:ConnectionString"] =
                    "InstrumentationKey=00000000-0000-0000-0000-000000000000;" +
                    "IngestionEndpoint=https://test.applicationinsights.azure.com/;",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Swap Azure-backed services for test doubles.
            services.RemoveAll<ISessionStore>();
            services.RemoveAll<IVerifiedIdClient>();
            services.RemoveAll<INotificationService>();

            services.AddSingleton<ISessionStore>(Sessions);
            services.AddSingleton(_verifiedIdMock.Object);
            services.AddSingleton(_notificationMock.Object);
        });
    }
}

// ── Integration tests ─────────────────────────────────────────────────────────

/// <summary>
/// Integration tests for the Verification API using an in-memory test server.
///
/// These tests exercise the full HTTP pipeline (routing, model binding, middleware)
/// without requiring any Azure services. They focus on the public (unauthenticated)
/// endpoints that are called by the caller's browser or by the Verified ID service.
/// </summary>
public class VerificationFlowTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public VerificationFlowTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ── PublicStatus ──────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that requesting status for a session that does not exist returns 404.
    /// The caller's browser polls this endpoint; a missing session should never
    /// silently return 200.
    /// </summary>
    [Fact]
    public async Task PublicStatus_UnknownSessionGuid_Returns404()
    {
        var unknownId = Guid.NewGuid().ToString();
        var response = await _client.GetAsync($"/api/verification/public-status/{unknownId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Same as above but with a non-GUID string to confirm the store lookup (not
    /// routing) is responsible for the 404.
    /// </summary>
    [Fact]
    public async Task PublicStatus_UnknownStringId_Returns404()
    {
        var response = await _client.GetAsync("/api/verification/public-status/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Initiate ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that submitting an initiate request without an email field returns 400.
    /// Model binding produces a null Email; the controller rejects it before any
    /// storage or Verified ID calls are made.
    /// </summary>
    [Fact]
    public async Task Initiate_MissingEmail_Returns400()
    {
        // Send a body that has only "code" — "email" is absent.
        var response = await _client.PostAsJsonAsync(
            "/api/verification/initiate",
            new { code = "ABCD1234" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Verifies that submitting an initiate request without a code field returns 400.
    /// </summary>
    [Fact]
    public async Task Initiate_MissingCode_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/verification/initiate",
            new { email = "caller@test.com" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Callback ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Callbacks without id_token are accepted — state-based correlation is the
    /// primary security mechanism (server-generated GUIDs). JWT is defense-in-depth.
    /// </summary>
    [Fact]
    public async Task Callback_MissingIdToken_Returns200_WithStateCorrelation()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/verification/callback",
            new
            {
                requestId = "some-request-id",
                requestStatus = "presentation_verified",
                state = Guid.NewGuid().ToString()
            });

        // Returns 200 OK — unknown session state is handled gracefully
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Callbacks with empty id_token at root level are accepted — the real id_token
    /// comes in receipt.id_token when includeReceipt=true.
    /// </summary>
    [Fact]
    public async Task Callback_EmptyIdToken_Returns200_ProcessedByState()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/verification/callback",
            new
            {
                id_token = "",
                requestId = "some-request-id",
                state = Guid.NewGuid().ToString()
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
