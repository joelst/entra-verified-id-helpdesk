# Copilot Instructions — Entra Verified ID Helpdesk

## Build, Test, and Lint

```bash
# Build the entire solution
dotnet build VerifiedIdHelpdesk.slnx

# Run all tests
dotnet test VerifiedIdHelpdesk.slnx

# Run a single test by name
dotnet test VerifiedIdHelpdesk.slnx --filter "FullyQualifiedName~CodeGeneratorTests.Generate_ReturnsCodeOfExactlyCodeLength"

# Run one test project
dotnet test tests/VerifiedIdHelpdesk.UnitTests

# Build in Release mode (CI uses this)
dotnet build VerifiedIdHelpdesk.slnx --configuration Release
```

Code analysis runs automatically via `Directory.Build.props` (`AnalysisMode: Recommended`). Fix analyzer warnings — CI builds with `--configuration Release`.

## Architecture

Three ASP.NET Core 10 web apps share a common backend:

- **AgentPortal** (MVC, port 5002) — Entra OIDC-authenticated helpdesk agent UI. Calls the Backend API via named `HttpClient("ApiClient")`.
- **VerifyPortal** (Razor Pages, port 5003) — Public, unauthenticated site where callers enter their code and approve a Verified ID presentation in Microsoft Authenticator.
- **Api** (Web API, port 5001) — Backend orchestration. Owns all business logic, session storage, SignalR hub, and the Entra Verified ID callback endpoint.

Shared libraries:

- **Core** — Domain models, interfaces (`ISessionStore`, `IVerifiedIdClient`, `INotificationService`), constants, exceptions. Zero external dependencies. Referenced by all projects.
- **Infrastructure** — Implementations: `AzureTableSessionStore`, `EntraVerifiedIdClient`, `CodeGenerator`, `CodeHasher`. References Core only.
- **Notifications** — `GraphNotificationService` (email via Graph `sendMail`, Teams via Graph chat API). References Core only.

### Request Flow

1. Agent calls `POST /api/verification/generate` → code generated, HMAC-hashed, session stored in Azure Table Storage, code sent to caller via email/Teams.
2. Caller submits email + code on VerifyPortal → `POST /api/verification/initiate` → code validated, Entra Verified ID presentation request created.
3. Caller approves in Authenticator → Entra Verified ID sends signed JWT callback to `POST /api/verification/callback`.
4. Api validates JWT signature, updates session to "verified", pushes result via SignalR to AgentPortal.

### Real-Time Updates

SignalR hub at `/hubs/verification`. Agent joins a group keyed by `sessionId`. On callback, the Api pushes `VerificationComplete` event. VerifyPortal uses polling (`GET /api/verification/public-status/{sessionId}`) as it has no SignalR connection.

### Authorization

Group-based access via Entra security group configured in `AuthorizationGroups:HelpDeskAgents`. The AgentPortal uses a custom `HelpDeskAgentHandler` that detects token groups claim overage (>200 groups) and falls back to Graph `checkMemberGroups` API.

## Key Conventions

### Security Rules (enforced in code and tests)

- **Never store or log the plaintext code.** Only `HMAC-SHA256(code, hmacKey)` is persisted. The plaintext is returned to the agent once, then discarded.
- **Use `RandomNumberGenerator`** for code generation — never `System.Random` or `Guid`.
- **Validate webhook JWT signatures** on every Entra Verified ID callback before touching the database.
- **All secrets from Key Vault** via `DefaultAzureCredential` and Managed Identity. No secrets in appsettings, env vars, or code.
- **Generic error messages** to public-facing endpoints — never expose internal details.
- **Rate limits**: max 5 failed code attempts per session, max 3 concurrent pending sessions per agent.
- **Idempotent callbacks**: check session status before updating; duplicate webhooks must not double-process.

### Configuration

All config flows through ASP.NET Core configuration with Key Vault as a provider. Non-secret values go in `appsettings.json`; secrets are read from Key Vault at startup. The Api project skips Key Vault in the `Testing` environment for integration tests.

Key config sections: `AzureAd`, `KeyVault`, `VerifiedId`, `Storage`, `AuthorizationGroups`, `AgentPortal`, `VerifyPortal`, `Api`, `Notifications`.

### DI Registration

Services are registered as singletons in `Program.cs` (no Startup class). Interface → implementation pattern:
- `ISessionStore` → `AzureTableSessionStore`
- `IVerifiedIdClient` → `EntraVerifiedIdClient`
- `INotificationService` → `GraphNotificationService`

The Api project aliases Core constants: `using CoreConstants = VerifiedIdHelpdesk.Core.Constants;`

### Testing Patterns

- **xUnit** with **Moq**. Global `<Using Include="Xunit" />` in the unit test project.
- Test names use `Method_ExpectedBehavior_WhenCondition` convention.
- Controller tests instantiate the controller directly with mocked dependencies — no HTTP middleware.
- Integration tests use `WebApplicationFactory<Program>` with environment set to `"Testing"` to skip Key Vault and App Insights. An `InMemorySessionStore` replaces Azure Table Storage.
- Test methods have XML doc comments explaining the security significance of what they verify.
- `.editorconfig` suppresses CA1707 (underscore identifiers) and CA1051 in test projects.

### Session Statuses

Sessions move through: `pending` → `verified` | `expired` | `failed`. Status string constants are in `SessionStatus` class. The `SessionExpiryService` background job runs every 2 minutes to expire stale sessions.

### Audit Logging

Structured log events via `ILogger` sent to Application Insights: `code_generated`, `verification_initiated`, `verification_completed`, `code_expired`. Email addresses are masked in logs for initiated events (`u***@contoso.com`).

### CSS / Theming

Both portals share a `theme.css` with CSS custom properties (`:root` variables) for all colors. No hex values outside `theme.css`. `site.css` in each portal is for app-specific overrides only. The code charset excludes visually confusable characters (0/O, 1/I/L) for phone readability.

### Project References

Core has no dependencies. Infrastructure and Notifications depend on Core. Api depends on all three. AgentPortal and VerifyPortal depend on Core only and call the Api over HTTP.
