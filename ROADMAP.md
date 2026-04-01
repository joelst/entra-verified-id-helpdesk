# Entra Verified ID Helpdesk — Roadmap

> Active backlog only. Completed milestones have been removed from this file.
> See `README.md` and `CHANGELOG.md` for shipped capabilities.
> Last updated: 2026-03-31

---

## Priority 0 — Correctness and Security

### 0.1 Fix the AgentPortal result/status contract

**Problem**

The agent-facing Result flow and the Pending-page polling fallback currently read the public status endpoint while expecting verified claims. The public endpoint is intentionally privacy-safe and must not return claims.

**Planned work**

- Change the agent Result flow to use an authorized agent-only status endpoint.
- Remove any assumption that `/api/verification/public-status/{sessionId}` returns claims.
- Update the pending fallback path so SignalR failure still leads to a correct agent result without depending on public PII.
- Add tests that prove the public endpoint stays claim-free while agents still see verified identity details.

### 0.2 Correct failed-attempt semantics on `/api/verification/initiate`

**Problem**

The current `FailedAttempts` behavior does not cleanly represent failed code-entry attempts. It also is not persisted before downstream request-creation failures, which weakens both the security story and operator expectations.

**Planned work**

- Redesign the lockout model so the documented "max 5 failed code attempts" rule matches the code path that enforces it.
- Persist attempt-related state before outbound Verified ID calls when needed.
- Add tests for invalid guesses, lockout behavior, and downstream failure handling.
- Update docs so the enforced rule is described precisely and consistently.

### 0.3 Make downstream auth failures visible to agents

**Problem**

Some AgentPortal pages still degrade to empty UI when downstream API auth fails, which makes token or API issues look like missing data instead of real failures.

**Planned work**

- Replace silent empty-state fallbacks with actionable error handling on user-initiated pages.
- Audit all AgentPortal proxy actions for the same pattern.
- Preserve non-interactive behavior only for background polling scenarios where it is intentional.
- Add coverage for 401, 403, and transient API failures.

### 0.4 Align instructions and operator docs with the current callback model

**Problem**

Some repo guidance still describes the older callback/JWT model and stale cookie guidance. That increases the chance of reintroducing already-fixed auth and callback regressions.

**Planned work**

- Update `CLAUDE.md`, deployment docs, and troubleshooting notes to match the current one-time callback token plus request-correlation model.
- Document that strict receipt JWT validation is optional and only relevant for `presentation_verified` when the tenant flow reliably provides it.
- Document the current AgentPortal cookie rationale so contributors do not revert it accidentally.

---

## Priority 1 — Test Coverage and Confidence

### 1.1 Add AgentPortal integration coverage

**Goal**

Cover the paths that have been most regression-prone and are currently lightly tested compared to the API.

**Planned work**

- Add tests for Result, Pending fallback behavior, History, and AccessDenied flows.
- Verify bearer-token forwarding on every agent-to-API proxy action.
- Add negative tests for empty or unauthorized agent responses so UI behavior is deliberate.

### 1.2 Expand callback and initiation negative-path tests

**Goal**

Prove the hardening logic under bad or incomplete inputs, not just the happy path.

**Planned work**

- Add coverage for forged or missing callback tokens.
- Add coverage for callback correlation edge cases and strict-mode behavior.
- Add tests for request-creation failures and attempt-state persistence.
- Keep public-endpoint PII-boundary tests explicit and negative.

### 1.3 Add startup configuration validation

**Goal**

Fail fast on missing or inconsistent configuration instead of discovering problems through runtime errors after deployment.

**Planned work**

- Validate required settings for `VerifiedId`, `AzureAd`, `Api`, `Notifications`, and `AuthorizationGroups` at startup.
- Surface configuration failures clearly in logs and readiness checks.
- Add environment-aware validation so `Testing` remains lightweight while deployed environments are strict.

---

## Priority 2 — Production Hardening

### 2.1 Replace the in-memory token cache with a distributed cache

**Goal**

Reduce restart-related auth churn and remove a known operational weak point in the AgentPortal.

**Planned work**

- Choose a durable cache backing store.
- Move MIWA token caching off `AddInMemoryTokenCaches()`.
- Validate restart behavior and token survivability under App Service recycle scenarios.

### 2.2 Remove stale external script allowances from VerifyPortal

**Goal**

Tighten the CSP and asset model so the VerifyPortal only allows what it actually needs.

**Planned work**

- Remove the remaining `cdn.jsdelivr.net` allowance if no runtime dependency remains.
- Bundle any remaining client assets locally.
- Add a CSP-focused smoke test to prevent the allowance from returning silently.

### 2.3 Add operational alerts and dashboards

**Goal**

Turn callback/auth regressions into fast-to-diagnose operational signals instead of support tickets.

**Planned work**

- Add Application Insights alerts for callback rejection spikes, repeated `MsalUiRequiredException` patterns, notification failures, and unusual session-expiry volume.
- Create a lightweight operator dashboard for verification throughput, failure reasons, and callback auth mode distribution.
- Document the main triage queries in `docs/troubleshooting.md`.

### 2.4 Validate Teams delivery end-to-end

**Goal**

Move Teams delivery from "implemented" to operationally trustworthy.

**Planned work**

- Validate 1:1 chat creation and message delivery against real tenant conditions.
- Handle recipient-not-found and chat-creation failure paths cleanly.
- Add operator guidance for sender identity setup and permissions.

---

## Priority 3 — Workflow Enhancements

### 3.1 Add resend and cancel session actions

**Goal**

Give agents better control over pending sessions without creating duplicate or confusing verification requests.

**Planned work**

- Add a resend action for supported delivery channels.
- Add an explicit cancel/expire action for sessions that should no longer remain pending.
- Ensure audit logging and rate limits remain correct for both actions.

### 3.2 Add deeper session and audit views for agents

**Goal**

Improve supportability without exposing public-facing PII.

**Planned work**

- Add a dedicated agent session detail view.
- Include callback outcome, delivery channel, timestamps, and verification result details.
- Keep the public surface area unchanged and privacy-safe.

### 3.3 Add supervisor/reporting capabilities

**Goal**

Support larger helpdesk teams with better oversight and troubleshooting.

**Planned work**

- Add supervisor-only policies and views.
- Provide team-level throughput and failure reporting.
- Keep group-based authorization aligned with the existing Entra-based model.

---

## Open Investigations

- Validate callback `requestId` behavior with real tenant traces and decide whether current enforcement should remain strict or become warning-only under specific callback shapes.
- Decide on the distributed cache backing store and operating model for App Service deployments.
- Revisit whether session lockout should be per session only, per caller email, or combined with stronger telemetry-driven abuse detection.

---

## Recommended Order

| Priority | Item                                  | Outcome                                                                                 |
| -------- | ------------------------------------- | --------------------------------------------------------------------------------------- |
| 1        | Fix agent result/status contract      | Agents reliably see verified identity without weakening the public API privacy boundary |
| 2        | Correct failed-attempt semantics      | The security model matches the documented lockout behavior                              |
| 3        | Surface downstream auth failures      | Auth issues stop presenting as misleading empty UI                                      |
| 4        | Align docs and instructions           | Contributors stop reintroducing stale callback or cookie guidance                       |
| 5        | Add AgentPortal integration coverage  | Highest-risk UI and auth paths gain regression protection                               |
| 6        | Expand negative-path API tests        | Callback and initiate hardening is validated under failure conditions                   |
| 7        | Add startup config validation         | Misconfiguration is caught before or during startup, not after deployment               |
| 8        | Introduce distributed token cache     | Restart-related auth issues are reduced materially                                      |
| 9        | Tighten VerifyPortal CSP              | The public portal has a smaller external attack surface                                 |
| 10       | Add operational alerts and dashboards | Callback and auth regressions become faster to detect and triage                        |
