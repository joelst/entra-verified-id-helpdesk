# Security Policy

## Supported Versions

Security fixes are applied to the latest release only. We do not backport fixes to older versions of this sample.

| Version | Supported |
|---------|-----------|
| Latest  | ✅        |
| Older   | ❌        |

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

If you believe you have found a security vulnerability in this sample, please report it using one of the following methods:

### Option 1: GitHub Private Security Advisory (preferred)

Use [GitHub's private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability) on this repository. This keeps the report confidential until a fix is available.

### Option 2: Email

Send a report to **security@example.com** with the subject line:

```
[SECURITY] entra-verified-id-helpdesk: <short description>
```

### What to include in your report

To help us triage quickly, please include as much of the following as possible:

- A description of the vulnerability and the potential impact
- The component(s) affected (e.g., `VerifiedIdHelpdesk.Api`, `infra/main.bicep`)
- Step-by-step instructions to reproduce the issue
- Proof-of-concept code or a demonstration (if safe to share)
- Any suggested mitigations you are aware of

### What to expect

| Timeline | Action |
|----------|--------|
| **Within 3 business days** | Acknowledgement of your report |
| **Within 10 business days** | Initial assessment and severity determination |
| **Within 90 days** | Fix released (for confirmed vulnerabilities) |

We follow [coordinated disclosure](https://en.wikipedia.org/wiki/Coordinated_vulnerability_disclosure). We ask that you give us time to investigate and release a fix before any public disclosure. We will credit reporters in the release notes unless you prefer to remain anonymous.

## Scope

This is a **sample application** intended to demonstrate integration patterns. In a production deployment, you are responsible for:

- Rotating the `EntraClientSecret` Key Vault secret on a regular schedule (or replacing it with a certificate)
- Restricting the Agent Portal to your corporate IP range (the Bicep parameter `corporateIpRange`)
- Applying App Service environment hardening beyond what the Bicep template provides
- Keeping all NuGet dependencies up to date

Vulnerabilities in **Azure services themselves** (Key Vault, App Service, Entra ID) should be reported directly to [Microsoft Security Response Center (MSRC)](https://msrc.microsoft.com/report/vulnerability).

## Security Design Principles

The following security properties are intentional and by design:

- One-time codes are never stored in plaintext — only an HMAC-SHA256 hash keyed by a secret from Key Vault
- Code generation uses `System.Security.Cryptography.RandomNumberGenerator`, never `System.Random`
- All secrets are sourced from Azure Key Vault via Managed Identity — none appear in code, configuration files, or environment variables
- Entra Verified ID webhook callbacks are validated by JWT signature before any database writes
- The session is marked `verified` before the callback response is returned (invalidates replay)
- Agent Portal enforces a corporate IP restriction via App Service access restrictions
- CORS on the Backend API is restricted to the Agent Portal and Verify Portal origins only
