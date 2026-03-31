# Entra Verified ID Helpdesk — Next Phase Roadmap

> **Status**: Core verification flow is working end-to-end ✅
> **Last updated**: 2026-03-31

---

## Phase 1 — Agent Experience Polish

### 1.1 Session History Page ✅
**Status**: Complete.

Agents can view their past verifications via a "History" nav link. `ISessionStore.GetByAgentAsync` queries Table Storage by `AgentEntraId`. The API exposes `GET /api/verification/my-sessions` (authorized, reads agent OID from JWT). `History.cshtml` displays caller, email, ticket, channel, status (color-coded badges), and time.

### 1.2 Concurrent Sessions Dashboard ✅
**Status**: Complete.

Agents can see all their active pending verifications on the Create page. Session cards show caller name, delivery channel, countdown timer, and link to the Pending page. A badge shows "N/3 active" and the submit button is disabled when at max capacity. `GET /api/verification/pending-sessions` endpoint serves the data, polled every 15 seconds via `active-sessions.js`.

### 1.3 Agent UX Improvements ✅
**Status**: Complete.

- **Step indicator**: 3-step breadcrumb (Generate Code → Awaiting Verification → Verified) on all views via `_StepIndicator.cshtml` partial
- **Expired session navigation**: "Start New Verification" button appears when code expires or session fails (both countdown-based and poll-based)
- **Validation summary fix**: Hidden `validation-summary-valid` div that was always rendering as a red box
- **Error handler path**: Fixed exception handler from `/Home/Error` (404) to `/Verification/Error`
- **MSAL re-auth**: `[AuthorizeForScopes]` on `Create` POST and `Result` actions to handle token cache expiry

---

## Phase 2 — Notification Channels

### 2.1 Email Delivery Testing & Fixes ✅
**Status**: Complete — verified working with real sender mailbox.

**Remaining polish**:
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

### 3.1 OBO Token Flow End-to-End ✅
**Status**: Complete.

The AgentPortal authenticates to the API using bearer tokens via the On-Behalf-Of flow. `[AuthorizeForScopes]` is applied to AgentPortal controller actions. `ITokenAcquisition` acquires tokens using `Api:Scopes`, and the `ApiClient` `HttpClient` forwards bearer tokens on every request. The API validates JWTs via `AddMicrosoftIdentityWebApi`.

### 3.2 Verified ID Callback JWT Validation ✅
**Status**: Complete.

The callback JWT from the Verified ID service is now fully validated:
- **Issuer**: Custom validator accepting both `login.microsoftonline.com` and `verifiedid.did.msidentity.com`
- **Audience**: Validated against the app client ID from configuration
- **Signature**: Validated against tenant signing keys
- **Lifetime**: Validated with a 5-minute clock skew tolerance

State-based session correlation remains as defense-in-depth alongside JWT validation.

### 3.3 Rate Limiting ✅
**Status**: Complete.

ASP.NET Core rate limiting middleware protects public endpoints:
- `/api/verification/initiate` — 10 requests/minute per IP
- `/api/verification/public-status` — 60 requests/minute per IP
- `/api/verification/callback` — 30 requests/minute per IP

Session-level limits are also enforced: max 5 failed code attempts per session, max 3 concurrent pending sessions per agent.

### 3.4 Security Headers Standardization ✅
**Status**: Complete.

Security headers applied to all three apps via middleware:
- `X-Frame-Options: DENY`
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: strict-origin`
- `Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=()`
- `X-Permitted-Cross-Domain-Policies: none`
- Content Security Policy on both portals (restrictive, no inline scripts)

### 3.5 Input Sanitization ✅
**Status**: Complete.

Graph search syntax injection fixed in AgentPortal `DirectoryController` — double quotes are stripped from user input before Graph API calls. Both AgentPortal and Api controllers sanitize input consistently.

### 3.6 Remaining Security Items
**Goal**: Address outstanding security hardening work.

**Completed**:
- ✅ **Cookie Hardening**: `HttpOnly=Always`, `Secure=Always`, `SameSite=Lax` (AgentPortal, for OIDC) / `Strict` (VerifyPortal, Api) configured via `CookiePolicyOptions`
- ✅ **Dependency Scanning**: Dependabot config (`.github/dependabot.yml`) scans NuGet + GitHub Actions weekly. PR dependency review workflow (`.github/workflows/dependency-review.yml`) checks for high-severity vulnerabilities and runs `dotnet list package --vulnerable`

**Remaining**:
- **Bundle CDN Dependencies**: VerifyPortal still loads scripts from `cdn.jsdelivr.net` — bundle locally to remove external dependency and tighten CSP
- **Distributed Token Cache**: Replace in-memory token cache with a distributed cache (relates to Phase 4.3)

---

## Phase 4 — Production Readiness

### 4.1 Bundle SignalR Locally ✅
**Status**: Complete.

SignalR JS client bundled locally in `wwwroot/lib/signalr/`. `Pending.cshtml` updated to reference local file. CDN dependency on `cdn.jsdelivr.net` removed from CSP `script-src`. Custom 404 handler also added.

### 4.2 Health Check Endpoints ✅
**Status**: Complete.

`/health` endpoint added to all three apps (basic liveness). `/ready` endpoint on the API checks Table Storage and Key Vault connectivity. App Service health check paths configured in Bicep.

### 4.3 Distributed Token Cache
**Goal**: Replace in-memory token cache so tokens survive app restarts.

**Plan**:
- Add Redis or Azure Table Storage-based distributed cache
- Configure MIWA to use `AddDistributedTokenCaches()` instead of `AddInMemoryTokenCaches()`
- This prevents the `user_null` error pattern if we ever switch back to delegated auth

### 4.4 Custom Error Pages ✅
**Status**: Complete.

Styled error pages for both portals. Exception handler path corrected to `/Verification/Error` with `[AllowAnonymous]` action. 404 handler added. No internal details leak in production error responses.

---

## Implementation Order (Recommended)

| Priority | Item | Effort | Status |
|----------|------|--------|--------|
| 1 | Session History (1.1) | Medium | ✅ Done |
| 2 | Agent UX Improvements (1.3) | Medium | ✅ Done |
| 3 | Concurrent Sessions (1.2) | Medium | ✅ Done |
| 4 | OBO Token Verification (3.1) | Medium | ✅ Done |
| 5 | Rate Limiting (3.3) | Small | ✅ Done |
| 6 | Callback JWT Validation (3.2) | Large | ✅ Done |
| 7 | Security Headers (3.4) | Small | ✅ Done |
| 8 | Input Sanitization (3.5) | Small | ✅ Done |
| 9 | Cookie Hardening (3.6) | Small | ✅ Done |
| 10 | Dependency Scanning (3.6) | Small | ✅ Done |
| 11 | Bundle SignalR (4.1) | Small | ✅ Done |
| 12 | Health Checks (4.2) | Small | ✅ Done |
| 13 | Error Pages (4.4) | Small | ✅ Done |
| 14 | Email Testing (2.1) | Small | ✅ Done |
| 15 | Teams Testing (2.2) | Small | Pending — needs sender user ID config |
| 16 | Bundle VerifyPortal CDN (3.6) | Small | Pending |
| 17 | Distributed Cache (4.3) | Medium | Pending — needs Redis/storage decision |

---

## Bug Fixes (this session)

- Verified ID API URL: removed tenant ID from URL path (API uses bearer token for tenant)
- Session expiry: fixed `DateTimeOffset` vs string comparison in Azure Table Storage filter
- Verified ID API error logging: full URL and response body now logged on failure

---

## What We Built (Night 1 — for context)

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
