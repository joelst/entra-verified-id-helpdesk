# Configuration Reference

Quick reference for all configuration settings across the three web applications: **Api**, **AgentPortal**, and **VerifyPortal**.

---

## How Configuration Works

ASP.NET Core loads configuration in the following order (last wins):

1. `appsettings.json` — base defaults checked into source control
2. `appsettings.{Environment}.json` — environment-specific overrides (e.g., `appsettings.Production.json`)
3. Environment variables
4. Azure App Service application settings
5. Azure Key Vault (loaded at startup via `KeyVault:Uri`)

> **Tip:** For environment variables and App Service settings, replace `:` with `__` (double underscore).
> For example, `AzureAd:TenantId` becomes `AzureAd__TenantId`.

> **Arrays:** Use numeric segments for array entries. For example, `Api:Scopes:0` becomes `Api__Scopes__0` in App Service or other environment-variable-based configuration sources.

---

## Application Settings

### AzureAd

Microsoft Entra ID authentication settings used by the OIDC middleware and confidential client.

| Setting                                                | Used By          | Description                              | Example Value                             |
| ------------------------------------------------------ | ---------------- | ---------------------------------------- | ----------------------------------------- |
| `AzureAd:Instance`                                     | All 3 apps       | Entra ID authority endpoint              | `https://login.microsoftonline.com/`      |
| `AzureAd:TenantId`                                     | All 3 apps       | Entra tenant GUID                        | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`    |
| `AzureAd:ClientId`                                     | Api, AgentPortal | App registration client ID               | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`    |
| `AzureAd:CallbackPath`                                 | AgentPortal      | OIDC redirect path                       | `/signin-oidc`                            |
| `AzureAd:ClientCertificates:0:SourceType`              | Api, AgentPortal | Certificate source — always `KeyVault`   | `KeyVault`                                |
| `AzureAd:ClientCertificates:0:KeyVaultUrl`             | Api, AgentPortal | Key Vault URI for the client certificate | `https://kv-vidhelpdesk.vault.azure.net/` |
| `AzureAd:ClientCertificates:0:KeyVaultCertificateName` | Api, AgentPortal | Certificate name in Key Vault            | `EntraClientCert`                         |

### KeyVault

| Setting        | Used By    | Description                                                   | Example Value                             |
| -------------- | ---------- | ------------------------------------------------------------- | ----------------------------------------- |
| `KeyVault:Uri` | All 3 apps | Key Vault URI — loads all secrets as configuration at startup | `https://kv-vidhelpdesk.vault.azure.net/` |

### VerifiedId

Settings for the Entra Verified ID service. **Api only.**

| Setting                                   | Used By      | Description                                                                                                                                                                                                                                                                                                       | Example Value                                 |
| ----------------------------------------- | ------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------- |
| `VerifiedId:TenantId`                     | Api          | Tenant issuing verified credentials                                                                                                                                                                                                                                                                               | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`        |
| `VerifiedId:ClientId`                     | Api          | App registration used for Verified ID calls                                                                                                                                                                                                                                                                       | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`        |
| `VerifiedId:DidAuthority`                 | Api          | Decentralized identifier for the organization                                                                                                                                                                                                                                                                     | `did:web:example.com`                         |
| `VerifiedId:CredentialType`               | Api          | Type of verifiable credential to request                                                                                                                                                                                                                                                                          | `VerifiedEmployee`                            |
| `VerifiedId:RequestServiceBaseUrl`        | Api          | Verified ID service endpoint                                                                                                                                                                                                                                                                                      | `https://verifiedid.did.msidentity.com/v1.0/` |
| `VerifiedId:RequireCallbackJwtValidation` | Api          | Optional. When `true`, successful `presentation_verified` callbacks must also include a valid `receipt.id_token`. Retrieval and error callbacks still rely on the one-time callback token plus `requestId` correlation. Leave `false` unless you have verified your tenant/wallet sends the receipt JWT reliably. | `false`                                       |
| `VerifiedId:EnrollmentUrl`                | VerifyPortal | Optional public URL where callers can create or obtain their organization's Verified ID before starting the verification flow.                                                                                                                                                                                    | `https://verify.contoso.com/create-id`        |

> **Note:** The API does not rely on a long-lived shared callback secret. It generates a one-time callback token per presentation request, sends it only to the Verified ID service through callback headers, and stores only the token hash with the session.

### Storage

| Setting              | Used By | Description                                   | Example Value                               |
| -------------------- | ------- | --------------------------------------------- | ------------------------------------------- |
| `Storage:AccountUri` | Api     | Azure Table Storage endpoint for session data | `https://<account>.table.core.windows.net/` |

### Authorization

| Setting                              | Used By          | Description                                          | Example Value                          |
| ------------------------------------ | ---------------- | ---------------------------------------------------- | -------------------------------------- |
| `AuthorizationGroups:HelpDeskAgents` | Api, AgentPortal | Entra security group Object ID for authorized agents | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` |

### Portal URLs

Cross-app URLs used for CORS, redirects, API calls, and Verified ID callback URL construction.

| Setting                | Used By                        | Description                                                                                                               | Example Value                                |
| ---------------------- | ------------------------------ | ------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------- |
| `AgentPortal:BaseUrl`  | Api                            | AgentPortal origin (for CORS)                                                                                             | `https://app-agentportal.azurewebsites.net`  |
| `VerifyPortal:BaseUrl` | Api, AgentPortal               | VerifyPortal origin for CORS in the Api, and the caller-facing portal URL shown in AgentPortal and notification messages. | `https://app-verifyportal.azurewebsites.net` |
| `Api:BaseUrl`          | AgentPortal, VerifyPortal, Api | Backend API base URL. The Api also uses this to build the callback URL sent to Verified ID.                               | `https://app-api.azurewebsites.net`          |
| `Api:Scopes`           | AgentPortal                    | OAuth scopes for calling the Api. In App Service, set the first entry as `Api__Scopes__0`.                                | `["api://<clientId>/access_as_agent"]`       |

> **App Service example:** If the AgentPortal calls the API with a single scope, set `Api__Scopes__0=api://<clientId>/access_as_agent`.

### Notifications

| Setting                      | Used By | Description                                                                                  | Example Value                          |
| ---------------------------- | ------- | -------------------------------------------------------------------------------------------- | -------------------------------------- |
| `Notifications:SenderEmail`  | Api     | UPN of the mailbox used to send verification codes and the current email fallback path       | `helpdesk@contoso.com`                 |
| `Notifications:SenderUserId` | Api     | Optional. Entra Object ID of the sender account; only needed if Teams delivery is re-enabled | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` |

### Telemetry

| Setting                                | Used By    | Description                                                            | Example Value                                  |
| -------------------------------------- | ---------- | ---------------------------------------------------------------------- | ---------------------------------------------- |
| `ApplicationInsights:ConnectionString` | All 3 apps | Application Insights connection string for telemetry and audit logging | `InstrumentationKey=...;IngestionEndpoint=...` |

---

## Key Vault Secrets

These secrets are stored in Azure Key Vault and loaded into configuration at startup.

| Secret            | Description                                                                                                                        | How to Generate                                                                                     |
| ----------------- | ---------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------- |
| `HmacKey`         | 32-byte base64-encoded key used for HMAC-SHA256 hashing of verification codes. The plaintext code is never stored — only the hash. | Generate with `RandomNumberGenerator`: `Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))` |
| `EntraClientCert` | Self-signed RSA certificate used as a client credential for Entra ID authentication (both Api and AgentPortal).                    | Run `scripts/Set-AppCertificate.ps1` to create and upload                                           |

---

## Application Constants

These values are hardcoded in `src/VerifiedIdHelpdesk.Core/Constants.cs` and require recompilation to change.

| Constant                     | Value                             | Description                                                                                                            |
| ---------------------------- | --------------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| `CodeCharset`                | `ABCDEFGHJKMNPQRSTUVWXYZ23456789` | Allowed characters for verification codes. Excludes visually confusable characters (0/O, 1/I/L) for phone readability. |
| `CodeLength`                 | `8`                               | Length of generated verification codes                                                                                 |
| `CodeExpiryMinutes`          | `10`                              | Minutes before an unused code expires                                                                                  |
| `MaxFailedAttempts`          | `5`                               | Session initiation lockout threshold used by the API before a session is marked failed                                 |
| `MaxPendingSessionsPerAgent` | `3`                               | Maximum concurrent pending verification sessions per agent                                                             |
| `SessionPartitionKey`        | `VerificationSession`             | Azure Table Storage partition key                                                                                      |
| `SessionTableName`           | `VerificationSessions`            | Azure Table Storage table name                                                                                         |
| `HelpDeskAgentPolicy`        | `HelpDeskAgent`                   | Authorization policy name                                                                                              |
| `VerificationHubPath`        | `/hubs/verification`              | SignalR hub endpoint path                                                                                              |

---

## Deployment Parameters (Bicep)

Settings configured at deployment time via `infra/main.bicep`.

| Parameter           | Default                                                | Description                                                                       |
| ------------------- | ------------------------------------------------------ | --------------------------------------------------------------------------------- |
| `suffix`            | *(required)*                                           | Unique suffix appended to all Azure resource names                                |
| `location`          | `resourceGroup().location`                             | Azure region for all resources                                                    |
| `tenantId`          | *(required)*                                           | Entra ID tenant GUID                                                              |
| `clientId`          | `''`                                                   | App registration client ID (set after initial registration)                       |
| `helpDeskGroupId`   | `''`                                                   | Entra security group Object ID for helpdesk agents                                |
| `corporateIpRange`  | `0.0.0.0/0`                                            | IP range for network restrictions — **restrict in production!**                   |
| `credentialType`    | `VerifiedEmployee`                                     | Verified credential type name                                                     |
| `didAuthority`      | *(required)*                                           | Organization's DID authority (`did:web:...`)                                      |
| `senderEmail`       | *(required)*                                           | UPN of the mailbox for sending verification codes                                 |
| `senderUserId`      | *(required)*                                           | Entra Object ID of the sender account                                             |
| `skuName`           | `S1`                                                   | App Service Plan SKU                                                              |
| `storageRedundancy` | `Standard_LRS`                                         | Storage account redundancy tier                                                   |
| `certName`          | `EntraClientCert`                                      | Name of the client certificate in Key Vault                                       |
| `keyVaultName`      | `''`                                                   | Optional custom Key Vault name if the default `kv-<suffix>` name is already taken |
| `repoUrl`           | `https://github.com/joelst/entra-verified-id-helpdesk` | GitHub repository URL for App Service deployment                                  |
| `repoBranch`        | `main`                                                 | Git branch for App Service deployment                                             |

---

## Settings by Application

Quick lookup — which settings does each app need.

### Api (Backend)

The Api is the most configuration-intensive app. It owns all business logic and external integrations.

- `AzureAd:Instance`, `AzureAd:TenantId`, `AzureAd:ClientId`
- `AzureAd:ClientCertificates:0:SourceType`, `AzureAd:ClientCertificates:0:KeyVaultUrl`, `AzureAd:ClientCertificates:0:KeyVaultCertificateName`
- `KeyVault:Uri`
- `VerifiedId:TenantId`, `VerifiedId:ClientId`, `VerifiedId:DidAuthority`, `VerifiedId:CredentialType`, `VerifiedId:RequestServiceBaseUrl`, `VerifiedId:RequireCallbackJwtValidation`
- `Storage:AccountUri`
- `AuthorizationGroups:HelpDeskAgents`
- `AgentPortal:BaseUrl`, `VerifyPortal:BaseUrl`, `Api:BaseUrl`
- `Notifications:SenderEmail`, `Notifications:SenderUserId`
- `ApplicationInsights:ConnectionString`

### AgentPortal (Helpdesk UI)

The AgentPortal authenticates agents via Entra ID and calls the Api over HTTP.

- `AzureAd:Instance`, `AzureAd:TenantId`, `AzureAd:ClientId`, `AzureAd:CallbackPath`
- `AzureAd:ClientCertificates:0:SourceType`, `AzureAd:ClientCertificates:0:KeyVaultUrl`, `AzureAd:ClientCertificates:0:KeyVaultCertificateName`
- `KeyVault:Uri`
- `AuthorizationGroups:HelpDeskAgents`
- `Api:BaseUrl`, `Api:Scopes`
- `ApplicationInsights:ConnectionString`

> **AgentPortal note:** Keep the global cookie minimum SameSite policy unspecified. OIDC correlation and nonce cookies need framework-managed `SameSite=None`; forcing `Lax` or `Strict` causes repeated sign-in prompts after the Entra redirect flow.

### VerifyPortal (Public)

The simplest configuration — no authentication, no direct storage access.

- `KeyVault:Uri`
- `Api:BaseUrl`
- `VerifiedId:EnrollmentUrl`
- `ApplicationInsights:ConnectionString`
