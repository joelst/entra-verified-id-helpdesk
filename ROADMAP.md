# Entra Verified ID Helpdesk — Next Phase Roadmap

> **Status**: Core verification flow is working end-to-end ✅
> **Last updated**: 2026-03-31

---

## Phase 1 — Agent Experience Polish

### 1.1 Session History Page
**Goal**: Agents can view their past verifications for audit trail and reference.

**What exists**: Sessions are already stored in Azure Table Storage with `AgentEntraId`, `CallerEmail`, `CallerDisplayName`, `TicketId`, `Status`, `CreatedAt`, `VerifiedAt`, `DeliveryChannel`.

**Plan**:
- Add `ISessionStore.GetByAgentAsync(string agentEntraId, int limit)` to query Table Storage by `AgentEntraId`
- Add `GET /api/verification/my-sessions` API endpoint (authorized, reads agent OID from JWT)
- Create `Views/Verification/History.cshtml` — table with columns: Caller, Email, Ticket, Channel, Status, Time
- Color-code status badges: 🟢 verified, 🔴 failed/expired, 🟡 pending
- Add "History" nav link in the AgentPortal header
- Pagination or "last 50" limit to keep it snappy

**Files to change**:
- `src/VerifiedIdHelpdesk.Core/Interfaces/ISessionStore.cs`
- `src/VerifiedIdHelpdesk.Infrastructure/AzureTableSessionStore.cs`
- `src/VerifiedIdHelpdesk.Api/Controllers/VerificationController.cs`
- `src/VerifiedIdHelpdesk.AgentPortal/Controllers/VerificationController.cs`
- New: `src/VerifiedIdHelpdesk.AgentPortal/Views/Verification/History.cshtml`
- `src/VerifiedIdHelpdesk.AgentPortal/Views/Shared/_Layout.cshtml` (nav link)

### 1.2 Concurrent Sessions Dashboard
**Goal**: Agents can manage multiple pending verifications simultaneously.

**What exists**: `MaxPendingSessionsPerAgent = 3` is enforced. The API already has `CountPendingByAgentAsync`. But the UI only shows one session at a time.

**Plan**:
- Add `ISessionStore.GetPendingByAgentAsync(string agentEntraId)` to return all pending sessions
- Add `GET /api/verification/pending-sessions` API endpoint
- On the Create page, show a sidebar or card strip of active pending sessions
- Each card: caller name, code (masked?), countdown timer, click to view
- Badge on the "Send Verification Request" button: "(2/3 active)"
- When a session completes or expires, remove its card via polling

**Files to change**:
- `src/VerifiedIdHelpdesk.Core/Interfaces/ISessionStore.cs`
- `src/VerifiedIdHelpdesk.Infrastructure/AzureTableSessionStore.cs`
- `src/VerifiedIdHelpdesk.Api/Controllers/VerificationController.cs`
- `src/VerifiedIdHelpdesk.AgentPortal/Views/Verification/Create.cshtml`
- New: `src/VerifiedIdHelpdesk.AgentPortal/wwwroot/js/active-sessions.js`
- `src/VerifiedIdHelpdesk.AgentPortal/wwwroot/css/site.css` (card styles)

---

## Phase 2 — Notification Channels

### 2.1 Email Delivery Testing & Fixes
**Goal**: Verify email delivery works end-to-end.

**Prerequisites**:
- `Notifications:SenderEmail` app setting on the API (a mailbox the app can send from)
- `Mail.Send` permission on the API managed identity (already granted)

**Plan**:
- Test with a real sender mailbox
- Verify the email template renders correctly (HTML in `GraphNotificationService.SendEmailAsync`)
- Add error handling: if email fails, return an actionable error to the agent
- Consider adding a "Resend" button on the Pending page

### 2.2 Teams Delivery Testing & Fixes
**Goal**: Verify Teams chat delivery works end-to-end.

**Prerequisites**:
- `Notifications:SenderUserId` app setting (Entra Object ID of the bot/service account)
- `Chat.Create` + `Chat.ReadWrite.All` permissions on the API managed identity (already granted)

**Plan**:
- Test 1:1 chat creation between the sender and recipient
- Verify the message renders with the code and verification portal link
- Handle edge cases: recipient not found, chat creation fails

---

## Phase 3 — Security Hardening

### 3.1 OBO Token Flow End-to-End
**Goal**: The AgentPortal authenticates to the API using bearer tokens, not anonymous calls.

**What exists**: `ITokenAcquisition` is injected, `GetApiAccessTokenAsync()` acquires tokens when `Api:Scopes` is configured. The API validates JWTs via `AddMicrosoftIdentityWebApi`.

**Plan**:
- Verify `Api:Scopes` app setting has the real client ID (not placeholder)
- Verify the app registration has `identifierUris` set and `access_as_agent` scope exposed
- Test that `/api/verification/generate` receives and validates the bearer token
- Move agent identity extraction from JWT claims (OBO provides the actual user's identity)
- Remove the `[AllowAnonymous]`-like behavior on generate endpoint

### 3.2 Verified ID Callback JWT Validation
**Goal**: Properly validate the callback JWT from the Verified ID service.

**Current state**: JWT validation logs a warning and continues (DID-based ES256 keys don't match standard Entra OIDC keys).

**Plan**:
- Research the correct JWKS endpoint for Verified ID callbacks (DID resolution)
- The `kid` is a `did:jwk:...` — the public key is embedded in the DID itself
- Parse the DID JWK from the `kid`, extract the EC public key, validate signature
- Add this as defense-in-depth alongside state-based session correlation

### 3.3 Rate Limiting
**Goal**: Protect public endpoints from abuse.

**Plan**:
- Add ASP.NET Core rate limiting middleware (`Microsoft.AspNetCore.RateLimiting`)
- `/api/verification/initiate` — 10 requests/minute per IP
- `/api/verification/public-status` — 60 requests/minute per IP
- `/api/verification/callback` — 30 requests/minute per IP (from Verified ID service)

---

## Phase 4 — Production Readiness

### 4.1 Bundle SignalR Locally
**Goal**: Remove CDN dependency for the SignalR JS client.

**Plan**:
- `npm install @microsoft/signalr`
- Copy `signalr.min.js` to `wwwroot/lib/signalr/`
- Update `Pending.cshtml` script reference
- Remove `cdn.jsdelivr.net` from CSP `script-src`

### 4.2 Health Check Endpoints
**Goal**: Enable Azure App Service health probes.

**Plan**:
- Add `/health` endpoint to each app (basic liveness)
- Add `/ready` to the API (checks Table Storage + Key Vault connectivity)
- Configure App Service health check paths in Bicep

### 4.3 Distributed Token Cache
**Goal**: Replace in-memory token cache so tokens survive app restarts.

**Plan**:
- Add Redis or Azure Table Storage-based distributed cache
- Configure MIWA to use `AddDistributedTokenCaches()` instead of `AddInMemoryTokenCaches()`
- This prevents the `user_null` error pattern if we ever switch back to delegated auth

### 4.4 Custom Error Pages
**Goal**: Friendly error pages instead of developer exception page in production.

**Plan**:
- Create styled 404 and 500 error pages for both portals
- Ensure no internal details leak in production error responses

---

## Implementation Order (Recommended)

| Priority | Item | Effort | Dependencies |
|----------|------|--------|-------------|
| 1 | Session History (1.1) | Medium | None |
| 2 | Email Testing (2.1) | Small | Sender mailbox configured |
| 3 | Teams Testing (2.2) | Small | Sender user ID configured |
| 4 | OBO Token Verification (3.1) | Medium | App registration scope setup |
| 5 | Concurrent Sessions (1.2) | Medium | Session History (reuses store methods) |
| 6 | Rate Limiting (3.3) | Small | None |
| 7 | Bundle SignalR (4.1) | Small | None |
| 8 | Health Checks (4.2) | Small | None |
| 9 | Callback JWT Validation (3.2) | Large | DID resolution research |
| 10 | Distributed Cache (4.3) | Medium | Redis or storage decision |
| 11 | Error Pages (4.4) | Small | None |

---

## What We Built Tonight (for context)

Starting from a `TypeLoadException` at deploy time, we fixed:
- NuGet package incompatibility (MicrosoftGraph → GraphServiceClient)
- PKCS#12 certificate format for MIWA
- Key Vault RBAC (secrets + certificates)
- App-only Graph auth (managed identity, not delegated)
- Least-privilege permissions (Graph on MIs, Verified ID on app reg)
- OBO token flow wiring
- CSP compliance (all inline scripts → external files)
- Verified ID client credentials auth (certificate from Key Vault)
- Callback endpoint format (receipt.id_token, not root id_token)
- Table Storage property size limits
- TempData cookie overflow (sessionId via query string)
- CORS origins (custom domains, trailing slash matching)
- Result page auth redirect loop
- Verbal delivery channel
- Placeholder logo, sign-out button, display name
- Email case normalization
- HTTP/2 enabled
