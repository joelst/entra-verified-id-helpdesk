# entra-verified-id-helpdesk

## What This App Does

This is a .NET 8 web application that enables an organization's helpdesk to verify caller identity using Microsoft Entra Verified ID. When an employee calls the helpdesk, an agent generates an 8-character one-time code and sends it to the caller via email or Teams. The caller navigates to a public verification site, enters their email and code, and approves a credential presentation in Microsoft Authenticator. The agent sees the verified identity in real time.

There are three web apps in one solution, plus shared libraries.

---

## Solution Structure

```
VerifiedIdHelpdesk.sln
├── src/
│   ├── VerifiedIdHelpdesk.AgentPortal/        # ASP.NET Core 8 MVC — helpdesk agent UI
│   ├── VerifiedIdHelpdesk.VerifyPortal/        # ASP.NET Core 8 Razor Pages — public IDVerify site
│   ├── VerifiedIdHelpdesk.Api/                 # ASP.NET Core 8 Web API — backend orchestration
│   ├── VerifiedIdHelpdesk.Core/                # Domain models, interfaces, constants (no dependencies)
│   ├── VerifiedIdHelpdesk.Infrastructure/      # Azure Table Storage, Entra Verified ID client, Graph notifications
│   └── VerifiedIdHelpdesk.Notifications/       # Email, Teams, SMS adapters
├── tests/
│   ├── VerifiedIdHelpdesk.UnitTests/
│   └── VerifiedIdHelpdesk.IntegrationTests/
└── infra/
    ├── main.bicep                           # All Azure resources
    └── parameters.json
```

---

## Tech Stack

- **.NET 10** — all projects target net10.0
- **ASP.NET Core 10 MVC** — Agent Portal
- **ASP.NET Core 10 Razor Pages** — IDVerify site (public, no auth)
- **ASP.NET Core 10 Web API** — Backend API
- **Microsoft.Identity.Web** — Entra ID OIDC authentication
- **Microsoft.Graph** — directory search, email, Teams notifications
- **Azure.Data.Tables** — session store
- **Azure.Security.KeyVault.Secrets** — secrets at runtime
- **Azure.Extensions.AspNetCore.Configuration.Secrets** — Key Vault as config provider
- **Microsoft.AspNetCore.SignalR** — real-time agent portal updates
- **Microsoft.ApplicationInsights.AspNetCore** — telemetry and audit logging

---

## NuGet Packages (all projects)

Add these to the relevant projects:

```xml
<!-- AgentPortal + Api -->
<PackageReference Include="Microsoft.Identity.Web" Version="*" />
<PackageReference Include="Microsoft.Identity.Web.MicrosoftGraph" Version="*" />
<PackageReference Include="Microsoft.Graph" Version="*" />
<PackageReference Include="Azure.Extensions.AspNetCore.Configuration.Secrets" Version="*" />
<PackageReference Include="Microsoft.ApplicationInsights.AspNetCore" Version="*" />

<!-- Api only -->
<PackageReference Include="Azure.Data.Tables" Version="*" />
<PackageReference Include="Azure.Security.KeyVault.Secrets" Version="*" />
<PackageReference Include="System.IdentityModel.Tokens.Jwt" Version="*" />

<!-- All web apps -->
<PackageReference Include="Microsoft.AspNetCore.SignalR" Version="*" />
```

Use the latest stable versions. Commit `packages.lock.json` for all projects.

---

## Configuration

All secrets come from Azure Key Vault via Managed Identity. No secrets in appsettings.json or environment variables.

### appsettings.json (non-secret values only)

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "<your-tenant-id>",
    "ClientId": "<app-registration-client-id>",
    "CallbackPath": "/signin-oidc"
  },
  "KeyVault": {
    "Uri": "https://<keyvault-name>.vault.azure.net/"
  },
  "VerifiedId": {
    "TenantId": "<your-tenant-id>",
    "ClientId": "<app-registration-client-id>",
    "DidAuthority": "did:web:<your-did-domain>",
    "CredentialType": "EmployeeVerifiedCredential",
    "RequestServiceBaseUrl": "https://verifiedid.did.msidentity.com/v1.0/"
  },
  "Storage": {
    "AccountUri": "https://<storageaccount>.table.core.windows.net/"
  },
  "AuthorizationGroups": {
    "HelpDeskAgents": "<entra-group-object-id>"
  },
  "AgentPortal": {
    "BaseUrl": "https://agents.<your-domain>"
  },
  "VerifyPortal": {
    "BaseUrl": "https://verify.<your-domain>"
  },
  "ApplicationInsights": {
    "ConnectionString": "<app-insights-connection-string>"
  }
}
```

### Key Vault secrets (set these manually or via Bicep)

| Secret name              | Value                                           |
|--------------------------|-------------------------------------------------|
| `EntraClientSecret`      | App registration client secret (POC only; use cert for prod) |
| `HmacKey`                | 32-byte base64 string for code hashing         |
| `StorageConnectionString`| Azure Table Storage connection string           |

### Program.cs — Key Vault config provider (add to all three web apps)

```csharp
builder.Configuration.AddAzureKeyVault(
    new Uri(builder.Configuration["KeyVault:Uri"]!),
    new DefaultAzureCredential());
```

---

## Project: VerifiedIdHelpdesk.Core

No external dependencies. Referenced by all other projects.

### Models/VerificationSession.cs

```csharp
public class VerificationSession
{
    public string SessionId { get; set; } = string.Empty;   // GUID — Table RowKey
    public string CodeHash { get; set; } = string.Empty;    // HMAC-SHA256 of code — NEVER store plaintext
    public string CallerEmail { get; set; } = string.Empty;
    public string CallerEntraId { get; set; } = string.Empty;
    public string CallerDisplayName { get; set; } = string.Empty;
    public string TicketId { get; set; } = string.Empty;    // Free text, not validated
    public string Note { get; set; } = string.Empty;        // Max 500 chars
    public string AgentEntraId { get; set; } = string.Empty;
    public string AgentDisplayName { get; set; } = string.Empty;
    public string DeliveryChannel { get; set; } = string.Empty; // "email" | "teams" | "sms"
    public string Status { get; set; } = "pending";         // pending | verified | expired | failed
    public string? VerifiedClaims { get; set; }             // JSON string of returned claims
    public string? RequestId { get; set; }                  // Entra Verified ID request ID
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }                 // CreatedAt + 10 minutes
    public DateTime? VerifiedAt { get; set; }
}
```

### Interfaces/ISessionStore.cs

```csharp
public interface ISessionStore
{
    Task<VerificationSession> CreateAsync(VerificationSession session);
    Task<VerificationSession?> GetAsync(string sessionId);
    Task<VerificationSession?> GetByCodeHashAsync(string codeHash, string callerEmail);
    Task UpdateAsync(VerificationSession session);
}
```

### Interfaces/IVerifiedIdClient.cs

```csharp
public interface IVerifiedIdClient
{
    Task<IssuanceRequestResult> CreateIssuanceRequestAsync(string userEmail, string idTokenHint);
    Task<PresentationRequestResult> CreatePresentationRequestAsync(string sessionId, string callbackUrl);
}
```

### Interfaces/INotificationService.cs

```csharp
public interface INotificationService
{
    Task SendCodeAsync(string recipientEmail, string displayCode, DateTime expiresAt, string channel);
}
```

### Constants.cs

```csharp
public static class Constants
{
    public const string CodeCharset = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    public const int CodeLength = 8;
    public const int CodeExpiryMinutes = 10;
    public const int MaxFailedAttempts = 5;
    public const int MaxPendingSessionsPerAgent = 3;
    public const string SessionPartitionKey = "VerificationSession";
    public const string HelpDeskAgentPolicy = "HelpDeskAgent";
    public const string VerificationHubPath = "/hubs/verification";
}
```

---

## Project: VerifiedIdHelpdesk.Infrastructure

### AzureTableSessionStore.cs

Uses `Azure.Data.Tables`. The table entity maps `PartitionKey = "VerificationSession"`, `RowKey = SessionId`.

Use `DefaultAzureCredential` to authenticate to Table Storage — no connection strings in code.

```csharp
var client = new TableClient(
    new Uri(config["Storage:AccountUri"]!),
    "VerificationSessions",
    new DefaultAzureCredential());
```

### CodeHasher.cs — CRITICAL SECURITY REQUIREMENT

```csharp
// NEVER store the plaintext code anywhere — only the hash.
// The HMAC key comes from Key Vault.
public static string Hash(string code, string hmacKeyBase64)
{
    var key = Convert.FromBase64String(hmacKeyBase64);
    var data = Encoding.UTF8.GetBytes(code.ToUpperInvariant());
    using var hmac = new HMACSHA256(key);
    return Convert.ToBase64String(hmac.ComputeHash(data));
}
```

### CodeGenerator.cs — CRITICAL SECURITY REQUIREMENT

```csharp
// MUST use RandomNumberGenerator — never System.Random, never Guid
public static string Generate()
{
    var charset = Constants.CodeCharset;
    var bytes = RandomNumberGenerator.GetBytes(Constants.CodeLength * 2);
    var result = new char[Constants.CodeLength];
    for (int i = 0; i < Constants.CodeLength; i++)
        result[i] = charset[bytes[i] % charset.Length];
    return new string(result);
}

public static string FormatForDisplay(string code) =>
    code[..4] + "-" + code[4..]; // "X7K2-PQ9R"
```

### EntraVerifiedIdClient.cs

Calls the Microsoft Entra Verified ID Request Service REST API.

Base URL: `https://verifiedid.did.msidentity.com/v1.0/{tenantId}/verifiableCredentials/`

**Issuance endpoint:** `POST /createIssuanceRequest`
- Use ID Token Hint pattern: include the user's Entra ID token as `idTokenHint`
- Returns a `requestId` and a URL/QR code for the Authenticator deep link

**Presentation endpoint:** `POST /createPresentationRequest`
- Include `callbackUrl` pointing to `/api/verification/callback`
- Include `requestedCredentials` array specifying the `EmployeeVerifiedCredential` type
- Returns a `requestId`, a QR code URL, and a deep link (`openid-vc://...`)

Authentication: use `DefaultAzureCredential` to acquire a token for scope `3db474b9-6a0c-4840-96ac-1fceb342124f/.default` (Verifiable Credentials service).

### GraphNotificationService.cs

Uses `Microsoft.Graph` SDK. Inject `GraphServiceClient` via `Microsoft.Identity.Web.MicrosoftGraph`.

**Email (sendMail):**
```csharp
await graphClient.Users[senderEmail].SendMail
    .PostAsync(new SendMailPostRequestBody { Message = message });
```

**Teams (find or create 1:1 chat, then send message):**
```csharp
// Use PostMessage approach — find chat by member, send to it
var chat = await graphClient.Chats.PostAsync(new Chat {
    ChatType = ChatType.OneOnOne,
    Members = new List<ConversationMember> { ... }
});
await graphClient.Chats[chat.Id].Messages.PostAsync(new ChatMessage {
    Body = new ItemBody { Content = messageText }
});
```

---

## Project: VerifiedIdHelpdesk.Api

### Program.cs

```csharp
var builder = WebApplication.CreateBuilder(args);

// Key Vault
builder.Configuration.AddAzureKeyVault(new Uri(builder.Configuration["KeyVault:Uri"]!), new DefaultAzureCredential());

// Auth — for agent-facing endpoints
builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration);

// Authorization — group policy
builder.Services.AddAuthorization(options => {
    options.AddPolicy(Constants.HelpDeskAgentPolicy, policy =>
        policy.RequireClaim("groups", builder.Configuration["AuthorizationGroups:HelpDeskAgents"]!));
});

// SignalR
builder.Services.AddSignalR();

// CORS — only allow Agent Portal and IDVerify site
builder.Services.AddCors(options => {
    options.AddPolicy("AllowPortals", policy => policy
        .WithOrigins(
            builder.Configuration["AgentPortal:BaseUrl"]!,
            builder.Configuration["VerifyPortal:BaseUrl"]!)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

// DI registrations
builder.Services.AddSingleton<ISessionStore, AzureTableSessionStore>();
builder.Services.AddSingleton<IVerifiedIdClient, EntraVerifiedIdClient>();
builder.Services.AddSingleton<INotificationService, GraphNotificationService>();
builder.Services.AddApplicationInsightsTelemetry();

var app = builder.Build();
app.UseCors("AllowPortals");
app.UseAuthentication();
app.UseAuthorization();
app.UseHttpsRedirection();
app.MapControllers();
app.MapHub<VerificationHub>(Constants.VerificationHubPath);
app.Run();
```

### Controllers/VerificationController.cs

```
POST /api/verification/generate
  [Authorize(Policy = HelpDeskAgentPolicy)]
  Body: { callerEntraId, callerEmail, callerDisplayName, ticketId, note, deliveryChannel }
  - Validate inputs
  - Check agent does not exceed MaxPendingSessionsPerAgent
  - Generate code via CodeGenerator.Generate()
  - Hash code via CodeHasher.Hash()
  - Create VerificationSession, save to ISessionStore
  - Send notification via INotificationService
  - Log "code_generated" event to Application Insights
  - Return: { sessionId, code (plaintext — shown to agent only), displayCode, expiresAt }

POST /api/verification/initiate
  [No auth — called from public IDVerify site, but only reachable via internal network]
  Body: { email, code }
  - Normalize: strip dashes/spaces, uppercase
  - Hash submitted code
  - Look up session by codeHash + email in ISessionStore
  - Validate: session exists, status == "pending", ExpiresAt > UtcNow
  - Increment attempt count; if > MaxFailedAttempts, set status = "failed", return 400
  - Call IVerifiedIdClient.CreatePresentationRequestAsync()
  - Save requestId to session
  - Log "verification_initiated" event
  - Return: { sessionId, requestId, qrCodeUri, deepLink, expiresAt }

GET /api/verification/status/{sessionId}
  [Authorize(Policy = HelpDeskAgentPolicy)]
  - Return: { status, verifiedClaims, verifiedAt }
  - Polling fallback for SignalR

POST /api/notification/send  (internal use)
  [Authorize — internal only]
  - Resend notification if delivery failed
```

### Controllers/CallbackController.cs

```
POST /api/verification/callback
  [No bearer auth — validated by Entra Verified ID callback signature]

  IMPORTANT: Validate the callback before processing.
  The callback includes an "id_token" that is a signed JWT.
  Validate it using the Entra Verified ID public keys (published at the DID document).
  Reject any callback that fails signature validation with HTTP 403.

  On valid callback:
  - Look up session by requestId
  - If status != "pending", return 200 (idempotent — ignore duplicates)
  - Set status = "verified"
  - Set VerifiedAt = UtcNow
  - Set VerifiedClaims = JSON of claims from callback (name, employeeId, department)
  - Save session
  - Push result to SignalR group keyed by sessionId
  - Log "verification_completed" event
  - Return: HTTP 200
```

### Hubs/VerificationHub.cs

```csharp
public class VerificationHub : Hub
{
    // Agent portal JavaScript connects and joins group by sessionId
    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }
}

// In CallbackController, inject IHubContext<VerificationHub> and push:
await hubContext.Clients.Group(sessionId).SendAsync("VerificationComplete", new {
    status = "verified",
    callerName = claims["displayName"],
    employeeId = claims["employeeId"],
    department = claims["department"],
    verifiedAt = DateTime.UtcNow
});
```

---

## Project: VerifiedIdHelpdesk.AgentPortal

### Program.cs

```csharp
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options => {
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy(Constants.HelpDeskAgentPolicy, policy =>
        policy.RequireClaim("groups", builder.Configuration["AuthorizationGroups:HelpDeskAgents"]!));
});

// All pages require authentication — agents who are not in the group see AccessDenied
```

### Group Membership Configuration

The group Object ID in `appsettings.json` is the only value that needs to change to point to a different Entra group:

```json
"AuthorizationGroups": {
  "HelpDeskAgents": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
}
```

**Step required in the app registration — do not skip:** Group claims are not included in Entra tokens by default. In the Azure portal, go to the app registration → **Token configuration** → **Add groups claim** → select **Security groups** → check **Group ID** for ID token and access token. Without this, `RequireClaim("groups", ...)` will never match and all agents will be denied.

Alternatively, add to the app registration manifest:
```json
"groupMembershipClaims": "SecurityGroup"
```

**Multiple access levels (optional):** To add supervisor or admin roles, add more groups and policies with no other code changes:

```json
"AuthorizationGroups": {
  "HelpDeskAgents":      "aaaa-...",
  "HelpDeskSupervisors": "bbbb-..."
}
```

```csharp
options.AddPolicy("HelpDeskAgent", policy =>
    policy.RequireClaim("groups", config["AuthorizationGroups:HelpDeskAgents"]!));
options.AddPolicy("HelpDeskSupervisor", policy =>
    policy.RequireClaim("groups", config["AuthorizationGroups:HelpDeskSupervisors"]!));
```

**Large group count overage (important for large organizations):** If a user belongs to more than 200 Entra groups, the `groups` claim is silently omitted from the token and replaced with a `_claim_names` / `_claim_sources` overage indicator. When this happens, `RequireClaim("groups", ...)` will never match even for legitimate agents, causing all of them to hit AccessDenied.

Detect overage by checking for `_claim_names` in the token claims. If present, fall back to a Graph API membership check instead of reading the claim:

```csharp
// In a custom IAuthorizationHandler or middleware
private async Task<bool> IsHelpDeskAgentAsync(ClaimsPrincipal user)
{
    // Check for overage — token groups claim was truncated
    if (user.HasClaim(c => c.Type == "_claim_names"))
    {
        // Fall back to Graph API check
        var userId = user.FindFirst("oid")?.Value
                  ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        var result = await _graphClient
            .Users[userId]
            .CheckMemberGroups
            .PostAsync(new CheckMemberGroupsPostRequestBody {
                GroupIds = new List<string> {
                    _config["AuthorizationGroups:HelpDeskAgents"]!
                }
            });

        return result?.Value?.Any() == true;
    }

    // Normal path — read groups claim from token
    return user.HasClaim("groups", _config["AuthorizationGroups:HelpDeskAgents"]!);
}
```

Register this as a scoped service and call it from the authorization policy handler. This pattern handles both normal and overage cases transparently. The Graph call requires `GroupMember.Read.All` (already in the required permissions list).

### Controllers/VerificationController.cs (Agent Portal MVC)

```
GET  /  → Redirect to /Verification/Create
GET  /Verification/Create → Render the request form (Entra directory search + ticket ID + note + channel)
POST /Verification/Create → Call Backend API /api/verification/generate, redirect to /Verification/Pending/{sessionId}
GET  /Verification/Pending/{sessionId} → Show waiting screen, connect SignalR, display code
GET  /Verification/Result/{sessionId} → Show verified identity (after SignalR pushes result)
GET  /AccessDenied → Shown to authenticated users not in the HelpDesk group
```

### Views — Key UX Requirements

**Create form** (`/Verification/Create`):
- Entra directory search: AJAX call to `/api/directory/search?q={query}` as the agent types — returns name, email, department from Microsoft Graph. Agent selects a result; the form stores `callerEntraId` and `callerEmail` as hidden fields.
- Ticket ID: plain text input, no validation
- Note: textarea, max 500 chars, optional
- Delivery channel: radio buttons — Email / Teams / SMS (SMS disabled with "(coming soon)" label)
- Submit button: "Send Verification Request"

**Pending screen** (`/Verification/Pending/{sessionId}`):
- Show the code prominently (formatted: `X7K2-PQ9R`) with a countdown timer (10 minutes, JavaScript)
- Show caller name, ticket ID, channel used
- Connect to SignalR hub, join group for sessionId
- On `VerificationComplete` event: redirect to `/Verification/Result/{sessionId}` or update the page in place
- Polling fallback: if SignalR fails to connect, poll `GET /api/verification/status/{sessionId}` every 3 seconds

**Result screen** (`/Verification/Result/{sessionId}`):
- Display verified identity prominently: Name, Employee ID, Department, Verified At
- Green success indicator
- Link to start a new verification

### Controllers/DirectoryController.cs (search endpoint)

```
GET /api/directory/search?q={query}
  [Authorize]
  - Call Microsoft Graph: GET /users?$search="displayName:{q}" OR "mail:{q}"
  - Requires ConsistencyLevel: eventual header
  - Return: [{ entraId, displayName, email, department, jobTitle }]
  - Max 10 results
```

---

## Project: VerifiedIdHelpdesk.VerifyPortal

This is the public-facing site. No authentication. URL: `https://verify.<your-domain>`

### Pages

**Index.cshtml** — Code entry page
- Form: email input + code input (8 chars, strips dashes on submit)
- Submit → POST to `/Verify`
- No login, no Entra SSO

**Verify.cshtml.cs** (OnPostAsync)
- Normalize code: strip spaces and dashes, uppercase
- POST to Backend API `/api/verification/initiate` with { email, code }
- On success: redirect to `/Verify/Present` with QR code URI and deep link in TempData
- On 400 (invalid code): show error message on Index page ("Code is invalid or has expired. Please contact your helpdesk agent.")
- NEVER expose internal error details to the user

**Present.cshtml** — Authenticator prompt page
- Show the QR code image (rendered from the base64 URI returned by the API)
- Show a prominent "Verify with Authenticator" button (deep link: `openid-vc://?request_uri=...`)
- Show countdown timer for remaining validity
- Polling: call `GET /api/verification/status/{sessionId}` every 3 seconds
- On `verified`: redirect to `/Verify/Complete`

**Complete.cshtml** — Success page
- "Your identity has been verified. You can return to your call."
- No identity claims shown (privacy — only the agent sees the verified claims)

### Security headers (add in Program.cs)

```csharp
app.Use(async (context, next) => {
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin");
    context.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:;");
    await next();
});
app.UseHsts();
```

---

## Background Job — Session Expiry

Add a hosted service (`IHostedService`) in the Api project that runs every 2 minutes and sets the status of expired sessions from `"pending"` to `"expired"`:

```csharp
public class SessionExpiryService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _sessionStore.ExpireOldSessionsAsync();
            // Log "code_expired" for each expired session
            await Task.Delay(TimeSpan.FromMinutes(2), ct);
        }
    }
}
```

---

## Audit Logging

Use `ILogger` with structured properties. All events go to Application Insights.

```csharp
// Example — code_generated
_logger.LogInformation("code_generated {@Event}", new {
    EventName = "code_generated",
    SessionId = session.SessionId,
    AgentEntraId = agentEntraId,
    AgentDisplayName = agentDisplayName,
    CallerEntraId = session.CallerEntraId,
    CallerEmail = session.CallerEmail,  // OK to log at generation
    TicketId = session.TicketId,
    DeliveryChannel = session.DeliveryChannel,
    ExpiresAt = session.ExpiresAt
});

// NEVER log the plaintext code — only the sessionId
// For verification_initiated, mask email: "u***@contoso.com"
```

---

## Security Rules — Do Not Violate

1. **Never store the plaintext code.** Only `HMAC-SHA256(code, hmacKey)`.
2. **Never log the plaintext code.** Log sessionId only.
3. **Use `RandomNumberGenerator`** for code generation — never `System.Random` or `Guid`.
4. **Validate webhook signatures** on every callback before touching the database.
5. **Validate code expiry server-side** — compare `ExpiresAt` (UTC) to `DateTime.UtcNow`. Never trust client timestamps.
6. **Invalidate the code on first use** — set status to `"verified"` before returning the callback response.
7. **All secrets from Key Vault via Managed Identity** — no secrets in appsettings, environment variables, or code.
8. **HMAC key never leaves Key Vault** — retrieve once at startup via config provider; store in memory only.
9. **Rate limit:** max 5 failed code attempts per session (then lock), max 3 concurrent pending sessions per agent.
10. **HTTPS only** — set `"HTTPS Only"` in App Service. Add HSTS header (min 1 year).
11. **CORS restricted** — Backend API allows only Agent Portal and IDVerify origins. No wildcards.
12. **Agent Portal restricted to corporate IP** — configure App Service access restrictions.
13. **Generic error messages to users** — never expose exception details, stack traces, or internal paths.
14. **Idempotent webhook handling** — check session status before updating; duplicate callbacks must not double-process.

---

## Development Setup

### Prerequisites

- .NET 8 SDK
- Azure CLI (`az login` with an account that has access to the dev Key Vault)
- Node.js (for any front-end tooling)
- Access to your organization's Entra tenant (for local auth to work)

### Local development

For local development, `DefaultAzureCredential` will use your `az login` credentials to access Key Vault and Table Storage. You need:

1. A dev Key Vault with the secrets listed above
2. Your account granted `Key Vault Secrets User` role on the dev vault
3. A dev Storage Account with a `VerificationSessions` table
4. An app registration in your Entra tenant with the permissions listed below

Set `KeyVault:Uri` in `appsettings.Development.json` to point to your dev vault.

For the Entra Verified ID Request Service, use a **dev credential type** so test issuances don't pollute production.

### App registration permissions required

| Permission | Type | Needed by |
|---|---|---|
| `User.Read.All` | Application | Backend API (directory search) |
| `GroupMember.Read.All` | Application | Backend API (group check) |
| `Mail.Send` | Application | Backend API (email notifications) |
| `Chat.Create` | Application | Backend API (Teams chat) |
| `ChatMessage.Send` | Application | Backend API (Teams message) |
| `VerifiableCredential.Create.All` | Application | Backend API (Verified ID API) |

---

## Azure Resources (Bicep — `infra/main.bicep`)

Create the following. Use Managed Identity for all resource access — no stored credentials.

| Resource | SKU |
|---|---|
| App Service Plan | B2 (P1v3 for prod) |
| App Service — Agent Portal | — |
| App Service — IDVerify Site | — |
| App Service — Backend API | — |
| Storage Account | Standard LRS |
| Key Vault | Standard |
| Application Insights | Pay-as-you-go |

Assign `Key Vault Secrets User` role to each App Service Managed Identity.
Assign `Storage Table Data Contributor` role to the Backend API Managed Identity.

---

## GitHub Samples — Start Here

Before writing code, clone and study these:

1. **Primary — Verified ID .NET:** `azure-samples/active-directory-verifiable-credentials-dotnet`
   - Folder `1-asp-net-core-api-idtokenhint` — issuance (use for self-service portal)
   - Presentation folders — verification flow (use for IDVerify site and Backend API)

2. **Auth pattern:** `Azure-Samples/active-directory-aspnetcore-webapp-openidconnect-v2`
   - Chapter 5 — group-based authorization (use for Agent Portal)

3. **Auth library:** `AzureAD/microsoft-identity-web` — read the README before configuring auth

4. **Graph SDK:** `microsoftgraph/msgraph-sdk-dotnet` — use for directory search, email, Teams

---

## Build Order

Build projects in this order (dependency chain):

1. `VerifiedIdHelpdesk.Core`
2. `VerifiedIdHelpdesk.Infrastructure` (depends on Core)
3. `VerifiedIdHelpdesk.Notifications` (depends on Core)
4. `VerifiedIdHelpdesk.Api` (depends on Core, Infrastructure, Notifications)
5. `VerifiedIdHelpdesk.AgentPortal` (depends on Core)
6. `VerifiedIdHelpdesk.VerifyPortal` (depends on Core)

---

## Theme and Branding

All three web apps share a single theme defined in one CSS file. Apply it consistently. Do not hardcode colors anywhere except in `theme.css`.

### File locations

```
src/
├── VerifiedIdHelpdesk.AgentPortal/wwwroot/
│   ├── css/theme.css          ← shared theme (copy or link from shared location)
│   ├── css/site.css           ← app-specific overrides only
│   └── images/logo.png        ← your organization logo (place your file here)
│
├── VerifiedIdHelpdesk.VerifyPortal/wwwroot/
│   ├── css/theme.css          ← same file, same content
│   ├── css/site.css
│   └── images/logo.png
```

Both sites reference the logo and theme identically so they look like one product.

---

### Color palette — CSS custom properties

Define all colors as CSS variables in `theme.css`. Every component uses these variables — no hex values anywhere else.

```css
/* theme.css — Entra Verified ID Helpdesk color palette */
:root {
  /* Primary brand */
  --color-primary:        #1B2A4A;   /* deep navy — headers, nav bars, primary buttons */
  --color-primary-dark:   #111E35;   /* darker navy — button hover, active states */
  --color-accent:         #0078D4;   /* Microsoft blue — links, highlights, accent bars */
  --color-accent-light:   #EBF3FC;   /* light blue tint — info boxes, selected rows */

  /* Status colors */
  --color-success:        #107C10;   /* green — verified state */
  --color-success-light:  #E8F8E8;   /* green tint — success backgrounds */
  --color-error:          #CC3300;   /* red — error messages */
  --color-warning:        #8B5E00;   /* amber — expiry warnings */

  /* Neutral */
  --color-text:           #2C2C2C;   /* near-black — body text */
  --color-text-muted:     #6B7280;   /* gray — secondary text, labels */
  --color-border:         #D0D7E0;   /* light border */
  --color-surface:        #F5F7FA;   /* off-white — card backgrounds, table rows */
  --color-white:          #FFFFFF;

  /* Typography */
  --font-family:          'Segoe UI', system-ui, -apple-system, sans-serif;
  --font-size-base:       14px;
  --font-size-lg:         16px;
  --font-size-sm:         12px;

  /* Spacing */
  --radius:               6px;       /* border radius for cards, inputs, buttons */
  --shadow:               0 2px 8px rgba(0, 0, 0, 0.08);
}
```

---

### Base layout — `_Layout.cshtml` (both portals)

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>@ViewData["Title"] — Identity Verification</title>
  <link rel="stylesheet" href="~/css/theme.css" />
  <link rel="stylesheet" href="~/css/site.css" />
</head>
<body>

  <!-- Top nav bar -->
  <header class="site-header">
    <div class="header-inner">
      <img src="~/images/logo.png" alt="Your Organization" class="logo" />
      <span class="app-title">Identity Verification</span>
      <!-- Agent Portal only: show signed-in agent name -->
      @if (User.Identity?.IsAuthenticated == true)
      {
        <span class="agent-name">@User.Identity.Name</span>
      }
    </div>
  </header>

  <!-- Page content -->
  <main class="main-content">
    @RenderBody()
  </main>

</body>
</html>
```

---

### Core component styles — `theme.css` (continued)

```css
/* ── Layout ─────────────────────────────── */
* { box-sizing: border-box; margin: 0; padding: 0; }

body {
  font-family: var(--font-family);
  font-size: var(--font-size-base);
  color: var(--color-text);
  background: var(--color-surface);
}

/* ── Header ─────────────────────────────── */
.site-header {
  background: var(--color-primary);
  border-bottom: 3px solid var(--color-accent);
  padding: 0 24px;
  height: 56px;
  display: flex;
  align-items: center;
}
.header-inner {
  display: flex;
  align-items: center;
  gap: 16px;
  width: 100%;
}
.logo {
  height: 32px;
  width: auto;
}
.app-title {
  color: #AABBCC;
  font-size: var(--font-size-lg);
  font-weight: 400;
}
.agent-name {
  margin-left: auto;
  color: var(--color-white);
  font-size: var(--font-size-sm);
}

/* ── Page wrapper ───────────────────────── */
.main-content {
  max-width: 800px;
  margin: 40px auto;
  padding: 0 24px;
}

/* ── Card ───────────────────────────────── */
.card {
  background: var(--color-white);
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  padding: 28px 32px;
  box-shadow: var(--shadow);
}
.card-title {
  font-size: 18px;
  font-weight: 600;
  color: var(--color-primary);
  margin-bottom: 20px;
  padding-bottom: 12px;
  border-bottom: 2px solid var(--color-accent);
}

/* ── Form inputs ────────────────────────── */
.form-group { margin-bottom: 20px; }
.form-label {
  display: block;
  font-weight: 600;
  color: var(--color-text);
  margin-bottom: 6px;
  font-size: var(--font-size-sm);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.form-control {
  width: 100%;
  padding: 10px 14px;
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  font-size: var(--font-size-base);
  font-family: var(--font-family);
  color: var(--color-text);
  background: var(--color-white);
  transition: border-color 0.15s;
}
.form-control:focus {
  outline: none;
  border-color: var(--color-accent);
  box-shadow: 0 0 0 3px rgba(0, 120, 212, 0.12);
}

/* ── Buttons ────────────────────────────── */
.btn {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 10px 24px;
  border: none;
  border-radius: var(--radius);
  font-size: var(--font-size-base);
  font-weight: 600;
  cursor: pointer;
  text-decoration: none;
  transition: background 0.15s, transform 0.1s;
}
.btn-primary {
  background: var(--color-primary);
  color: var(--color-white);
}
.btn-primary:hover  { background: var(--color-primary-dark); }
.btn-accent {
  background: var(--color-accent);
  color: var(--color-white);
}
.btn-accent:hover   { background: #006CBF; }
.btn:active         { transform: scale(0.98); }

/* ── Status badges ──────────────────────── */
.badge {
  display: inline-block;
  padding: 3px 10px;
  border-radius: 99px;
  font-size: var(--font-size-sm);
  font-weight: 600;
}
.badge-pending   { background: var(--color-accent-light);  color: var(--color-accent); }
.badge-verified  { background: var(--color-success-light); color: var(--color-success); }
.badge-expired   { background: #F3F4F6;                    color: var(--color-text-muted); }
.badge-failed    { background: #FEE8E8;                    color: var(--color-error); }

/* ── Verification code display ──────────── */
.code-display {
  font-family: 'Courier New', monospace;
  font-size: 36px;
  font-weight: 700;
  letter-spacing: 0.12em;
  color: var(--color-primary);
  background: var(--color-accent-light);
  border: 2px dashed var(--color-accent);
  border-radius: var(--radius);
  padding: 18px 32px;
  text-align: center;
  margin: 20px 0;
}

/* ── Verified result panel ──────────────── */
.result-verified {
  border-left: 5px solid var(--color-success);
  background: var(--color-success-light);
  border-radius: var(--radius);
  padding: 20px 24px;
}
.result-verified .result-name {
  font-size: 20px;
  font-weight: 700;
  color: var(--color-success);
}
.result-verified .result-meta {
  color: var(--color-text-muted);
  font-size: var(--font-size-sm);
  margin-top: 4px;
}

/* ── Alert / info box ───────────────────── */
.alert {
  padding: 12px 16px;
  border-radius: var(--radius);
  font-size: var(--font-size-base);
  margin-bottom: 16px;
}
.alert-info    { background: var(--color-accent-light); border-left: 4px solid var(--color-accent); }
.alert-error   { background: #FEE8E8;                   border-left: 4px solid var(--color-error); }
.alert-warning { background: #FFF8E1;                   border-left: 4px solid #F59E0B; }

/* ── Countdown timer ────────────────────── */
.countdown {
  font-size: var(--font-size-sm);
  color: var(--color-text-muted);
  text-align: center;
}
.countdown.expiring-soon { color: var(--color-error); font-weight: 600; }

/* ── Directory search dropdown ──────────── */
.search-results {
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  background: var(--color-white);
  box-shadow: var(--shadow);
  max-height: 200px;
  overflow-y: auto;
  margin-top: 4px;
}
.search-result-item {
  padding: 10px 14px;
  cursor: pointer;
  border-bottom: 1px solid var(--color-surface);
}
.search-result-item:hover { background: var(--color-accent-light); }
.search-result-name  { font-weight: 600; color: var(--color-text); }
.search-result-meta  { font-size: var(--font-size-sm); color: var(--color-text-muted); }
```

---

### Customizing the palette

To change the color scheme, edit only the `:root` block at the top of `theme.css`. Every component picks up the change automatically.

To swap to your organization's brand color (e.g., `#D40000`):
```css
:root {
  --color-primary:      #D40000;
  --color-primary-dark: #AA0000;
  /* leave all other variables as-is */
}
```

---

### Logo

Place the logo file at `wwwroot/images/logo.png` in both portals.

- Preferred format: PNG with transparent background
- Recommended size: 160 × 48px (will render at `height: 32px` in the header)
- If the logo is white/light (for dark backgrounds): use it as-is — the header is dark navy
- Do not hardcode logo dimensions in CSS — the `.logo { height: 32px; width: auto; }` rule handles it

If no logo file is provided, hide the element gracefully:
```css
.logo { display: none; }
.app-title { font-weight: 600; color: var(--color-white); }
```

---

## What to Build First (Recommended Order)

1. **Core models and interfaces** — no dependencies, unblocks everything
2. **Infrastructure — AzureTableSessionStore** — needed by Api
3. **Infrastructure — CodeGenerator + CodeHasher** — needed by Api
4. **Backend API — /api/verification/generate** — test with Postman/curl
5. **IDVerify Portal — Index + Verify pages** — test code entry
6. **Infrastructure — EntraVerifiedIdClient** — needs Verified ID tenant configured
7. **Backend API — /api/verification/initiate + /api/verification/callback** — full flow
8. **Agent Portal — Create form + Pending screen with SignalR** — end-to-end visible
9. **GraphNotificationService** — email and Teams delivery
10. **Session expiry background service**
11. **Tests**
12. **Bicep infra**
