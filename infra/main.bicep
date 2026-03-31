// ============================================================
// main.bicep — Entra Verified ID Helpdesk Azure Infrastructure
//
// Deploys all Azure resources using Managed Identity exclusively.
// No credentials are stored anywhere — secrets are placed in Key
// Vault manually after deployment (see README § Deployment).
//
// Usage:
//   az deployment group create \
//     --resource-group <rg> \
//     --template-file infra/main.bicep \
//     --parameters @infra/parameters.json
// ============================================================

// ── Parameters ────────────────────────────────────────────────────────────────

@description('Unique suffix appended to every resource name (e.g. "helpdesk-prod"). Keep it short: Key Vault names are capped at 24 characters.')
param suffix string

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Microsoft Entra tenant ID.')
param tenantId string

@description('App registration client ID (used by all three apps). Can be left empty and updated later via "az webapp config appsettings set".')
param clientId string = ''

@description('Object ID of the Entra security group whose members are helpdesk agents. Can be left empty and updated later.')
param helpDeskGroupId string = ''

// CUSTOMIZE: Set to your corporate CIDR range to restrict Agent Portal access.
// The default '0.0.0.0/0' leaves the portal open — update this before production.
@description('IP CIDR range allowed to reach the Agent Portal. Use your corporate egress IP range.')
param corporateIpRange string = '0.0.0.0/0'

@description('Verifiable credential type name defined in your Entra Verified ID tenant.')
param credentialType string = 'EmployeeVerifiedCredential'

@description('DID authority for your Verified ID tenant, e.g. "did:web:yourdomain.com".')
param didAuthority string

@description('UPN of the mailbox used to send email notifications via Microsoft Graph.')
param senderEmail string

@description('Entra object ID of the sender user account.')
param senderUserId string

@description('App Service plan SKU. S1 (Standard) is the recommended default — no special quota required and ~40% cheaper than P1v3. B2 (Basic tier) requires Basic VM quota which many subscriptions do not have.')
@allowed(['S1', 'S2', 'S3', 'P0v3', 'P1v3', 'P2v3', 'B2'])
param skuName string = 'S1'

@description('Storage account redundancy. LRS for dev/test; ZRS or GRS for production.')
@allowed(['Standard_LRS', 'Standard_ZRS', 'Standard_GRS'])
param storageRedundancy string = 'Standard_LRS'

@description('Name of the Entra app registration certificate in Key Vault. Must match the -CertName used in New-AppRegistration.ps1.')
param certName string = 'EntraClientCert'

@description('Custom storage account name.Leave empty to auto-generate from the suffix (e.g. suffix "helpdesk-dev" → "sthelpdesdev"). Storage account names must be globally unique, 3–24 lowercase letters and digits only — no hyphens.')
@maxLength(24)
param storageAccountName string = ''

@description('GitHub repository URL to deploy application code from. Defaults to the canonical repo. Change this if you have forked the repository.')
param repoUrl string = 'https://github.com/joelst/entra-verified-id-helpdesk'

@description('Git branch to deploy from.')
param repoBranch string = 'main'

@description('Enable Always-On for all App Services. Recommended for production to prevent cold starts; disable for dev/test to reduce costs.')
param alwaysOn bool = false

// ── Naming variables ──────────────────────────────────────────────────────────
//
// CUSTOMIZE: Adjust these naming conventions to match your organisation's standards.
// Key Vault max length = 24 chars. Storage account max = 24 chars, lowercase only.

var planName    = 'asp-${suffix}'
var apiName     = 'app-api-${suffix}'
var agentsName  = 'app-agents-${suffix}'
var verifyName  = 'app-verify-${suffix}'
var storageName = empty(storageAccountName) ? 'st${replace(suffix, '-', '')}' : storageAccountName // no hyphens; must be lowercase
var kvName      = 'kv-${suffix}'                    // max 24 chars total
var appiName    = 'appi-${suffix}'
var logName     = 'log-${suffix}'

// Pre-compute base URLs from resource names to avoid circular resource dependencies.
// These follow the App Service default hostname pattern.
var apiBaseUrl    = 'https://${apiName}.azurewebsites.net'
var agentsBaseUrl = 'https://${agentsName}.azurewebsites.net'
var verifyBaseUrl = 'https://${verifyName}.azurewebsites.net'

var tags = {
  Application: 'VerifiedIdHelpdesk'
  ManagedBy: 'Bicep'
}

// Derive App Service plan tier from SKU name
var skuTier = startsWith(skuName, 'P') ? 'PremiumV3' : startsWith(skuName, 'S') ? 'Standard' : 'Basic'

// ── Log Analytics Workspace ────────────────────────────────────────────────────

resource logWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logName
  location: location
  tags: tags
  properties: {
    retentionInDays: 30 // CUSTOMIZE: Increase for compliance requirements (max 730)
    sku: {
      name: 'PerGB2018'
    }
  }
}

// ── Application Insights (workspace-based) ─────────────────────────────────────

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appiName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logWorkspace.id
    RetentionInDays: 30
  }
}

// ── Storage Account ────────────────────────────────────────────────────────────
// Only the Backend API Managed Identity has Table Data Contributor access.
// No public blob access; TLS 1.2 minimum.

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  tags: tags
  sku: {
    name: storageRedundancy // CUSTOMIZE: Standard_ZRS or Standard_GRS for production resilience
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

// ── Key Vault ──────────────────────────────────────────────────────────────────
// RBAC auth model (not access policies). Soft delete retained for 90 days.
// After deployment, run New-AppRegistration.ps1 to create the certificate in Key Vault,
// then set the HmacKey secret manually:
//   HmacKey — 32-byte base64 key for HMAC-SHA256 one-time code hashing

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard' // CUSTOMIZE: Use 'premium' for HSM-backed keys
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true  // Managed Identity role assignments handle access
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
  }
}

// ── App Service Plan ───────────────────────────────────────────────────────────
// CUSTOMIZE: Change SKU to P2v3 for high-traffic production (auto-scale, custom domains, slots).
// B2 (Basic tier) requires Basic VM quota — many subscriptions have none. Use P1v3 unless you
// have confirmed Basic VM quota in your subscription.

resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  sku: {
    name: skuName  // S1 default (Standard tier); P1v3/P2v3 for production; B2 requires Basic VM quota
    tier: skuTier
  }
  kind: 'linux'
  properties: {
    reserved: true // Required for Linux-hosted apps
  }
}

// ── Backend API App Service ────────────────────────────────────────────────────
// Hosts the ASP.NET Core Web API. All secrets are read from Key Vault via Managed Identity.
// All config values that are non-secret are baked in as app settings here.

resource apiApp 'Microsoft.Web/sites@2024-04-01' = {
  name: apiName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      alwaysOn: alwaysOn
      linuxFxVersion: 'DOTNETCORE|10.0' // CUSTOMIZE: Update when new LTS is available
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT',                value: 'Production' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'KeyVault__Uri',                         value: keyVault.properties.vaultUri }
        { name: 'AzureAd__Instance',                     value: 'https://login.microsoftonline.com/' }
        { name: 'AzureAd__TenantId',                     value: tenantId }
        { name: 'AzureAd__ClientId',                     value: clientId }
        { name: 'VerifiedId__TenantId',                  value: tenantId }
        { name: 'VerifiedId__ClientId',                  value: clientId }
        { name: 'VerifiedId__DidAuthority',              value: didAuthority }
        { name: 'VerifiedId__CredentialType',            value: credentialType }
        { name: 'VerifiedId__RequestServiceBaseUrl',     value: 'https://verifiedid.did.msidentity.com/v1.0/' }
        { name: 'Storage__AccountUri',                   value: storageAccount.properties.primaryEndpoints.table }
        { name: 'AuthorizationGroups__HelpDeskAgents',   value: helpDeskGroupId }
        { name: 'AgentPortal__BaseUrl',                  value: agentsBaseUrl }
        { name: 'VerifyPortal__BaseUrl',                 value: verifyBaseUrl }
        { name: 'Api__BaseUrl',                          value: apiBaseUrl }
        { name: 'Notifications__SenderEmail',            value: senderEmail }
        { name: 'Notifications__SenderUserId',           value: senderUserId }
        // Certificate-based auth: M.I.W. reads the full PFX via the KV Secrets endpoint
        { name: 'AzureAd__ClientCertificates__0__SourceType',              value: 'KeyVault' }
        { name: 'AzureAd__ClientCertificates__0__KeyVaultUrl',             value: keyVault.properties.vaultUri }
        { name: 'AzureAd__ClientCertificates__0__KeyVaultCertificateName', value: certName }
        // Oryx build: tell the build system which project to compile in this monorepo
        { name: 'PROJECT',                               value: 'src/VerifiedIdHelpdesk.Api/VerifiedIdHelpdesk.Api.csproj' }
        { name: 'SCM_DO_BUILD_DURING_DEPLOYMENT',        value: 'true' }
      ]
    }
  }
}

// ── Agent Portal App Service ───────────────────────────────────────────────────
// Internal-facing MVC app. IP-restricted to the corporate network.
// CUSTOMIZE: Set corporateIpRange parameter to your corporate egress CIDR.

resource agentsApp 'Microsoft.Web/sites@2024-04-01' = {
  name: agentsName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      alwaysOn: alwaysOn
      linuxFxVersion: 'DOTNETCORE|10.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT',                value: 'Production' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'KeyVault__Uri',                         value: keyVault.properties.vaultUri }
        { name: 'AzureAd__Instance',                     value: 'https://login.microsoftonline.com/' }
        { name: 'AzureAd__TenantId',                     value: tenantId }
        { name: 'AzureAd__ClientId',                     value: clientId }
        { name: 'AuthorizationGroups__HelpDeskAgents',   value: helpDeskGroupId }
        { name: 'Api__BaseUrl',                          value: apiBaseUrl }
        { name: 'Api__Scopes__0',                        value: 'api://${clientId}/access_as_agent' }
        // Certificate-based auth
        { name: 'AzureAd__ClientCertificates__0__SourceType',              value: 'KeyVault' }
        { name: 'AzureAd__ClientCertificates__0__KeyVaultUrl',             value: keyVault.properties.vaultUri }
        { name: 'AzureAd__ClientCertificates__0__KeyVaultCertificateName', value: certName }
        // Oryx build
        { name: 'PROJECT',                               value: 'src/VerifiedIdHelpdesk.AgentPortal/VerifiedIdHelpdesk.AgentPortal.csproj' }
        { name: 'SCM_DO_BUILD_DURING_DEPLOYMENT',        value: 'true' }
      ]
      // IP restriction: allow only the corporate IP range.
      // When corporateIpRange is the default '0.0.0.0/0' no restriction is applied.
      ipSecurityRestrictions: corporateIpRange == '0.0.0.0/0' ? [] : [
        {
          ipAddress: corporateIpRange
          action: 'Allow'
          priority: 100
          name: 'AllowCorporate'
          description: 'Allow only from corporate network' // CUSTOMIZE: Update description
        }
      ]
    }
  }
}

// ── Verify Portal App Service ──────────────────────────────────────────────────
// Public-facing Razor Pages app — no authentication, no IP restriction.
// The caller navigates here to enter their code and approve in Authenticator.

resource verifyApp 'Microsoft.Web/sites@2024-04-01' = {
  name: verifyName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      alwaysOn: alwaysOn
      linuxFxVersion: 'DOTNETCORE|10.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT',                value: 'Production' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'KeyVault__Uri',                         value: keyVault.properties.vaultUri }
        { name: 'Api__BaseUrl',                          value: apiBaseUrl }
        // Oryx build
        { name: 'PROJECT',                               value: 'src/VerifiedIdHelpdesk.VerifyPortal/VerifiedIdHelpdesk.VerifyPortal.csproj' }
        { name: 'SCM_DO_BUILD_DURING_DEPLOYMENT',        value: 'true' }
      ]
    }
  }
}


// ── Source Control / Oryx Build ───────────────────────────────────────────────
//
// Each App Service pulls from the GitHub repository and builds with Oryx when
// the Bicep deployment runs. Because these are child resources of the site, the
// app settings (including PROJECT and SCM_DO_BUILD_DURING_DEPLOYMENT) are always
// applied before the build is triggered — no race condition.
//
// isManualIntegration: true = no webhook; deploy is triggered by ARM/Bicep.
// To re-deploy without re-provisioning infrastructure, use the "Sync" button in
// App Service → Deployment Center, or re-run `az deployment group create`.
//
// The PROJECT app setting on each site tells Oryx which .csproj to build.

resource apiSourceControl 'Microsoft.Web/sites/sourcecontrols@2024-04-01' = {
  parent: apiApp
  name: 'web'
  properties: {
    repoUrl: repoUrl
    branch: repoBranch
    isManualIntegration: true
    deploymentRollbackEnabled: false
    isMercurial: false
  }
}

resource agentsSourceControl 'Microsoft.Web/sites/sourcecontrols@2024-04-01' = {
  parent: agentsApp
  name: 'web'
  properties: {
    repoUrl: repoUrl
    branch: repoBranch
    isManualIntegration: true
    deploymentRollbackEnabled: false
    isMercurial: false
  }
}

resource verifySourceControl 'Microsoft.Web/sites/sourcecontrols@2024-04-01' = {
  parent: verifyApp
  name: 'web'
  properties: {
    repoUrl: repoUrl
    branch: repoBranch
    isManualIntegration: true
    deploymentRollbackEnabled: false
    isMercurial: false
  }
}


// ── RBAC Role Assignments ──────────────────────────────────────────────────────
//
// Key Vault Secrets User (4633458b-…) — read-only access to Key Vault secrets.
// Key Vault Certificate User (db79e9a7-…) — read certificates (needed by MIWA's
//   KeyVaultCertificateLoader which calls CertificateClient.GetCertificateAsync).
// Storage Table Data Contributor (0a9a7e1f-…) — read/write Table Storage entities.
//
// Only the API needs Storage access. All three apps need Key Vault secrets access.
// AgentPortal and API also need certificate read access to load the Entra client cert.

var kvSecretsUserRoleId           = '4633458b-17de-408a-b874-0445c86b69e6'
var kvCertificateUserRoleId       = 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba'
var storageTableContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

resource apiKvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, apiApp.id, kvSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: apiApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource agentsKvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, agentsApp.id, kvSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: agentsApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource verifyKvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, verifyApp.id, kvSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: verifyApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Certificate User — AgentPortal and API load the Entra client certificate from Key Vault
resource apiKvCertRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, apiApp.id, kvCertificateUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvCertificateUserRoleId)
    principalId: apiApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource agentsKvCertRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, agentsApp.id, kvCertificateUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvCertificateUserRoleId)
    principalId: agentsApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource apiStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, apiApp.id, storageTableContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableContributorRoleId)
    principalId: apiApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ── Outputs ────────────────────────────────────────────────────────────────────

@description('HTTPS URL of the Backend API App Service.')
output apiUrl string = 'https://${apiApp.properties.defaultHostName}'

@description('HTTPS URL of the Agent Portal App Service.')
output agentPortalUrl string = 'https://${agentsApp.properties.defaultHostName}'

@description('HTTPS URL of the Verify Portal App Service.')
output verifyPortalUrl string = 'https://${verifyApp.properties.defaultHostName}'

@description('Key Vault name (for post-deployment secret injection).')
output keyVaultName string = keyVault.name

@description('Key Vault URI (matches KeyVault__Uri app setting).')
output keyVaultUri string = keyVault.properties.vaultUri

@description('Storage Account name.')
output storageAccountName string = storageAccount.name

@description('Application Insights connection string.')
output appInsightsConnectionString string = appInsights.properties.ConnectionString
