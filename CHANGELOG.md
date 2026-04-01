# Changelog

All notable changes to the Entra Verified ID Helpdesk will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [1.0.0] — 2026-03-31

### Core Features
- Three-app architecture: AgentPortal (MVC), VerifyPortal (Razor Pages), Api (Web API)
- One-time code generation with HMAC-SHA256 hashing (codes never stored in plaintext)
- Entra Verified ID presentation request and callback flow
- Real-time agent notifications via SignalR
- Email and Teams notification delivery via Microsoft Graph
- Verbal code delivery channel
- Directory search with Graph API typeahead

### Agent Experience
- Session history page with status badges and filtering
- Concurrent sessions dashboard (up to 3 active, with live cards)
- Step indicator breadcrumb (Generate → Awaiting → Verified)
- Expired session navigation with "Start New Verification" button

### Security
- Entra OIDC authentication with group-based authorization
- Group claim overage handling (200+ groups fallback to Graph API)
- JWT callback validation (issuer, audience, signature, lifetime)
- Rate limiting on public API endpoints
- Security headers on all apps (CSP, X-Frame-Options, HSTS, etc.)
- Cookie hardening (HttpOnly, Secure, SameSite)
- Input sanitization (Graph search injection prevention)
- CORS restricted to portal origins only
- Managed Identity for all Azure service access
- Key Vault for all secrets (no hardcoded credentials)

### Infrastructure
- Bicep IaC with Deploy to Azure button
- Health check endpoints on all apps
- Application Insights telemetry with structured audit logging
- Dependabot + dependency review GitHub Actions workflow
- Bootstrap 5.3.8, SignalR bundled locally (no CDN dependencies)

### Documentation
- Comprehensive README with architecture diagram
- App settings reference (docs/app-settings.md)
- Fork and deploy guide (docs/fork-and-deploy.md)
- SECURITY.md with responsible disclosure process
- CONTRIBUTING.md with development setup
