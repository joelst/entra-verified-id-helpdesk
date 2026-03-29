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

@description('App registration client ID (used by all three apps).')
param clientId string

@description('Object ID of the Entra security group whose members are helpdesk agents.')
param helpDeskGroupId string

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

// ── Naming variables ──────────────────────────────────────────────────────────
//
// CUSTOMIZE: Adjust these naming conventions to match your organisation's standards.
// Key Vault max length = 24 chars. Storage account max = 24 chars, lowercase only.

var planName    = 'asp-${suffix}'
var apiName     = 'app-api-${suffix}'
var agentsName  = 'app-agents-${suffix}'
var verifyName  = 'app-verify-${suffix}'
var storageName = 'st${replace(suffix, '-', '')}' // no hyphens; must be lowercase
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
    name: 'Standard_LRS' // CUSTOMIZE: Use ZRS or GRS for production resilience
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
// After deployment, set the two secrets manually:
//   EntraClientSecret — app registration client secret
//   HmacKey           — 32-byte base64 key for HMAC-SHA256 code hashing

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

// ── App Service Plan (Linux, B2) ───────────────────────────────────────────────
// CUSTOMIZE: Change SKU to P1v3/PremiumV3 for production (auto-scale, custom domains, slots).

resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  sku: {
    name: 'B2'   // CUSTOMIZE: 'P1v3' for production
    tier: 'Basic' // CUSTOMIZE: 'PremiumV3' for production
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
      linuxFxVersion: 'DOTNETCORE|10.0' // CUSTOMIZE: Update when new LTS is available
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
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
        { name: 'Notifications__SenderEmail',            value: senderEmail }
        { name: 'Notifications__SenderUserId',           value: senderUserId }
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
      linuxFxVersion: 'DOTNETCORE|10.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT',                value: 'Production' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'KeyVault__Uri',                         value: keyVault.properties.vaultUri }
        { name: 'AzureAd__Instance',                     value: 'https://login.microsoftonline.com/' }
        { name: 'AzureAd__TenantId',                     value: tenantId }
        { name: 'AzureAd__ClientId',                     value: clientId }
        { name: 'AuthorizationGroups__HelpDeskAgents',   value: helpDeskGroupId }
        { name: 'Api__BaseUrl',                          value: apiBaseUrl }
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
      linuxFxVersion: 'DOTNETCORE|10.0'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT',                value: 'Production' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'KeyVault__Uri',                         value: keyVault.properties.vaultUri }
        { name: 'Api__BaseUrl',                          value: apiBaseUrl }
      ]
    }
  }
}

// ── RBAC Role Assignments ──────────────────────────────────────────────────────
//
// Key Vault Secrets User (4633458b-…) — read-only access to Key Vault secrets.
// Storage Table Data Contributor (0a9a7e1f-…) — read/write Table Storage entities.
//
// Only the API needs Storage access. All three apps need Key Vault access to load
// the HMAC key and (for AgentPortal/API) the Entra client secret.

var kvSecretsUserRoleId          = '4633458b-17de-408a-b874-0445c86b69e6'
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
