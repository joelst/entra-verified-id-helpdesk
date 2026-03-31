<#
.SYNOPSIS
    Creates an Entra app registration for the Verified ID Helpdesk application.

.DESCRIPTION
    This script automates the steps in the "Entra App Registration Setup" section of the README:
      - Creates a single-tenant app registration
      - Adds API permissions: Entra Verified ID (VerifiableCredential.Create.All) and the
        self-referential access_as_agent delegated scope for OBO flow
      - Grants admin consent for Verified ID
      - Configures Security Group claims in the token
      - Optionally creates a self-signed certificate in Key Vault and registers it with the app

    Microsoft Graph permissions are NOT added to the app registration. Instead, they are
    granted directly to the App Service managed identities after infrastructure deployment
    using Grant-ManagedIdentityPermissions.ps1. This follows the least-privilege principle —
    only the managed identities that need Graph access receive it.

    Use -SkipCert to skip the Key Vault certificate step and run Set-AppCertificate.ps1 later
    (after Azure infrastructure has been deployed). This allows you to create the app registration
    first, deploy infrastructure with the known clientId, then add the certificate.

    A certificate is used instead of a client secret: the private key is generated inside
    Key Vault and never exported. The app reads the certificate directly from Key Vault at
    runtime using Microsoft.Identity.Web's KeyVault certificate source.

    Prerequisites:
      - PowerShell 7+
      - Azure CLI installed and logged in (az login)
      - You must be a Global Administrator or Privileged Role Administrator to grant admin consent
      - Key Vault Certificates Officer role on the target vault (required for certificate creation,
        only needed if NOT using -SkipCert)

.PARAMETER DisplayName
    Display name for the app registration. Default: "VerifiedID Helpdesk"

.PARAMETER AgentPortalUrl
    Base URL of the Agent Portal used to build the redirect URI.
    Default: "https://localhost:5002"
    For Azure deployments use "https://app-agents-<suffix>.azurewebsites.net"

.PARAMETER CertValidityMonths
    Number of months the self-signed certificate is valid for. Default: 24
    Only used when -SkipCert is not specified.

.PARAMETER KeyVaultName
    Name of the Azure Key Vault where the certificate will be created.
    Required when -SkipCert is not specified.

.PARAMETER CertName
    Name of the certificate in Key Vault. Default: "EntraClientCert"
    Only used when -SkipCert is not specified.

.PARAMETER SkipCert
    Skip Key Vault certificate creation. Use this when deploying infrastructure first —
    run Set-AppCertificate.ps1 after the Key Vault has been provisioned.

.EXAMPLE
    # Step 1 of cloud deployment: create app reg first, deploy infra second
    .\New-AppRegistration.ps1 `
        -AgentPortalUrl "https://app-agents-helpdesk-prod.azurewebsites.net" `
        -SkipCert

    # Step 3 of cloud deployment: add cert after infra is deployed
    .\Set-AppCertificate.ps1 -KeyVaultName kv-helpdesk-prod -AppId <clientId from above>

.EXAMPLE
    # All-in-one (requires Key Vault to already exist)
    .\New-AppRegistration.ps1 -KeyVaultName kv-helpdesk-prod `
        -AgentPortalUrl "https://app-agents-helpdesk-prod.azurewebsites.net"

.EXAMPLE
    # Production with a custom name, public URL, and 36-month certificate
    .\New-AppRegistration.ps1 -DisplayName "Helpdesk Verified ID" `
        -AgentPortalUrl "https://agents.contoso.com" `
        -KeyVaultName kv-helpdesk-prod `
        -CertValidityMonths 36
#>
[CmdletBinding()]
param(
    [string] $DisplayName        = 'VerifiedID Helpdesk',
    [string] $AgentPortalUrl     = 'https://localhost:5002',
    [int]    $CertValidityMonths = 24,
    [string] $KeyVaultName       = '',
    [string] $CertName           = 'EntraClientCert',

    # Path where the certificate PEM (cert + private key) is saved for local development.
    # Set to '' or use -SkipLocalDevCert to skip the export.
    [string] $LocalDevCertPath   = "$HOME/.entra-vidhelp/$CertName.pfx",

    # Skip automatic Key Vault role assignment (use if you have already assigned the role
    # or prefer to assign it yourself before running the script)
    [switch] $SkipRoleAssignment,

    # Skip exporting the certificate PEM for local development
    [switch] $SkipLocalDevCert,

    # Skip Key Vault certificate creation entirely. Use when deploying infrastructure first.
    # Run Set-AppCertificate.ps1 after the Key Vault has been provisioned.
    [switch] $SkipCert
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Step([string]$Message) {
    Write-Information ''
    Write-Information ">> $Message"
}

function Get-AppRoleId([PSCustomObject]$ServicePrincipal, [string]$PermissionName) {
    $role = $ServicePrincipal.appRoles |
        Where-Object { $_.value -eq $PermissionName -and $_.allowedMemberTypes -contains 'Application' }
    if (-not $role) {
        throw "Permission '$PermissionName' not found on '$($ServicePrincipal.appDisplayName)'"
    }
    return $role.id
}

# ---------------------------------------------------------------------------
# Verify Azure CLI login
# ---------------------------------------------------------------------------

Write-Step 'Checking Azure CLI login'
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Error "Not logged in to Azure CLI. Run 'az login' first."
    exit 1
}
Write-Information "  Signed in as : $($account.user.name)"
Write-Information "  Tenant       : $($account.tenantId)"
Write-Information "  Subscription : $($account.name)"

# ---------------------------------------------------------------------------
# Look up service principal for Entra Verified ID
# ---------------------------------------------------------------------------

Write-Step 'Looking up service principals'

# Microsoft Entra Verified ID (Azure AD Verifiable Credentials)
$VerifiedIdAppId = '3db474b9-6a0c-4840-96ac-1fceb342124f'

$vcSp = az ad sp show --id $VerifiedIdAppId 2>$null | ConvertFrom-Json
if (-not $vcSp) {
    Write-Warning "Entra Verified ID service principal not found. You will need to add 'VerifiableCredential.Create.All' manually after the script finishes."
}
else {
    Write-Information "  Found: $($vcSp.appDisplayName)"
}

# ---------------------------------------------------------------------------
# Resolve permission IDs dynamically from the service principal manifests
# ---------------------------------------------------------------------------

Write-Step 'Resolving API permission IDs'

# Verified ID permission is resolved now; the self-referential OBO scope is added after
# app creation (it needs $appId and $scopeId which don't exist yet).
$vcPermId = $null
if ($vcSp) {
    $vcPermId = Get-AppRoleId $vcSp 'VerifiableCredential.Create.All'
    Write-Information '  Verified ID permission resolved: VerifiableCredential.Create.All'
}
Write-Information '  Graph permissions are NOT on the app registration (granted to managed identities instead)'

# ---------------------------------------------------------------------------
# Create the app registration
# ---------------------------------------------------------------------------

Write-Step "Creating app registration '$DisplayName'"

$redirectUri = "$AgentPortalUrl/signin-oidc"

$app = az ad app create `
    --display-name $DisplayName `
    --sign-in-audience AzureADMyOrg `
    --web-redirect-uris $redirectUri `
    --query '{ appId: appId, id: id }' | ConvertFrom-Json

$appId = $app.appId
$appObjId = $app.id
Write-Information "  Application (client) ID : $appId"
Write-Information "  Object ID               : $appObjId"

# ---------------------------------------------------------------------------
# Expose an API scope — required for OBO token flow (AgentPortal → API)
# ---------------------------------------------------------------------------

Write-Step 'Exposing API scope for OBO token flow'

$scopeId = [System.Guid]::NewGuid().ToString()
$apiScopeJson = @{
    requestedAccessTokenVersion = 2
    oauth2PermissionScopes      = @(
        @{
            id                      = $scopeId
            adminConsentDescription = 'Allows the Agent Portal to call the backend API on behalf of the signed-in user.'
            adminConsentDisplayName = 'Access Helpdesk API as agent'
            isEnabled               = $true
            type                    = 'User'
            userConsentDescription  = 'Allows the Agent Portal to call the backend API on your behalf.'
            userConsentDisplayName  = 'Access Helpdesk API'
            value                   = 'access_as_agent'
        }
    )
} | ConvertTo-Json -Depth 10 -Compress

$apiTempFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "$([System.Guid]::NewGuid()).json")
$apiScopeJson | Set-Content $apiTempFile -Encoding UTF8

az ad app update --id $appId --identifier-uris "api://$appId" --set "api=@$apiTempFile"
Remove-Item $apiTempFile

# Pre-authorize the app to use its own scope (same-app OBO)
az ad app update --id $appId `
    --set "api.preAuthorizedApplications=[{""appId"":""$appId"",""delegatedPermissionIds"":[""$scopeId""]}]"

Write-Information "  Application ID URI : api://$appId"
Write-Information "  Scope              : access_as_agent (id: $scopeId)"

# ---------------------------------------------------------------------------
# Create the service principal so admin-consent can target it
# ---------------------------------------------------------------------------

Write-Step 'Creating service principal'
az ad sp create --id $appId | Out-Null
Write-Information '  Done'

# ---------------------------------------------------------------------------
# Build required resource access (all permissions at once)
# ---------------------------------------------------------------------------

# Only the self-referential OBO scope and Verified ID permission are on the app registration.
# Graph permissions are granted directly to managed identities via Grant-ManagedIdentityPermissions.ps1.
$requiredAccess = @(
    @{
        resourceAppId  = $appId
        resourceAccess = @(@{ id = $scopeId; type = 'Scope' })
    }
)

if ($vcPermId) {
    $requiredAccess += @{
        resourceAppId  = $VerifiedIdAppId
        resourceAccess = @(@{ id = $vcPermId; type = 'Role' })
    }
}

# ---------------------------------------------------------------------------
# Set required resource access (all permissions at once)
# ---------------------------------------------------------------------------

Write-Step 'Adding API permissions'

$requiredAccessJson = $requiredAccess | ConvertTo-Json -Depth 10 -Compress
$tempFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "$([System.Guid]::NewGuid()).json")
$requiredAccessJson | Set-Content $tempFile -Encoding UTF8

az ad app update --id $appId --required-resource-accesses "@$tempFile"
Remove-Item $tempFile

Write-Information '  Permissions set'

# ---------------------------------------------------------------------------
# Grant admin consent for Verified ID
# ---------------------------------------------------------------------------

Write-Step 'Granting admin consent'
Write-Information '  Waiting a few seconds for the service principal to propagate...'
Start-Sleep -Seconds 10

# Only Verified ID needs admin consent on the app registration.
# Graph permissions are granted to managed identities separately.
if ($vcSp) {
    az ad app permission admin-consent --id $appId
    Write-Information '  Admin consent granted for Verified ID'
}
else {
    Write-Information '  Skipped — Verified ID service principal not found (grant consent manually)'
}

# ---------------------------------------------------------------------------
# Configure Security Group claims
# ---------------------------------------------------------------------------

Write-Step 'Configuring Security Group claims in token'
az ad app update --id $appId --set groupMembershipClaims="SecurityGroup"
Write-Information '  groupMembershipClaims set to SecurityGroup'

# ---------------------------------------------------------------------------
# Certificate creation (skip if -SkipCert or no KeyVaultName)
# ---------------------------------------------------------------------------

if ($SkipCert -or -not $KeyVaultName) {
    if (-not $SkipCert -and -not $KeyVaultName) {
        Write-Warning 'No -KeyVaultName specified and -SkipCert not set. Skipping certificate creation.'
        Write-Warning 'Run Set-AppCertificate.ps1 after deploying infrastructure to add the certificate.'
    }
}
else {
    # Delegate to Set-AppCertificate.ps1 (same directory as this script)
    $setCertScript = Join-Path $PSScriptRoot 'Set-AppCertificate.ps1'
    if (-not (Test-Path $setCertScript)) {
        Write-Error "Set-AppCertificate.ps1 not found at '$setCertScript'. Run it manually after deployment."
        exit 1
    }

    $certParams = @{
        AppId              = $appId
        KeyVaultName       = $KeyVaultName
        CertName           = $CertName
        CertValidityMonths = $CertValidityMonths
        DisplayName        = $DisplayName
    }
    if ($SkipRoleAssignment) { $certParams['SkipRoleAssignment'] = $true }
    if ($SkipLocalDevCert)   { $certParams['SkipLocalDevCert']   = $true }
    if ($LocalDevCertPath)   { $certParams['LocalDevCertPath']   = $LocalDevCertPath }

    & $setCertScript @certParams
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$tenantId = $account.tenantId

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

Write-Output ''
Write-Output '======================================================'
Write-Output '  App registration complete!'
Write-Output '======================================================'
Write-Output ''
Write-Output "  Application (client) ID : $appId"
Write-Output "  Tenant ID               : $tenantId"
Write-Output ''

if ($SkipCert -or -not $KeyVaultName) {
    Write-Output '-- Next steps ----------------------------------------'
    Write-Output '  1. Deploy Azure infrastructure (Bicep), then run:'
    Write-Output "     .\scripts\Set-AppCertificate.ps1 -KeyVaultName <kv-name> -AppId $appId"
    Write-Output '  2. Grant Graph permissions to managed identities:'
    Write-Output '     .\scripts\Grant-ManagedIdentityPermissions.ps1 -ResourceGroupName <rg> -Suffix <suffix>'
    Write-Output ''
}
else {
    Write-Output '-- appsettings.json ----------------------------------'
    Write-Output "  AzureAd:TenantId                                 = $tenantId"
    Write-Output "  AzureAd:ClientId                                 = $appId"
    Write-Output "  AzureAd:ClientCertificates:0:SourceType          = KeyVault"
    Write-Output "  AzureAd:ClientCertificates:0:KeyVaultUrl         = https://$KeyVaultName.vault.azure.net/"
    Write-Output "  AzureAd:ClientCertificates:0:KeyVaultCertificateName = $CertName"
    Write-Output "  VerifiedId:TenantId                              = $tenantId"
    Write-Output "  VerifiedId:ClientId                              = $appId"
    Write-Output "  Api:Scopes:0                                     = api://$appId/access_as_agent"
    Write-Output ''
    Write-Output '-- App Service Application Settings (env vars) -------'
    Write-Output "  AzureAd__TenantId=$tenantId"
    Write-Output "  AzureAd__ClientId=$appId"
    Write-Output "  AzureAd__ClientCertificates__0__SourceType=KeyVault"
    Write-Output "  AzureAd__ClientCertificates__0__KeyVaultUrl=https://$KeyVaultName.vault.azure.net/"
    Write-Output "  AzureAd__ClientCertificates__0__KeyVaultCertificateName=$CertName"
    Write-Output "  VerifiedId__TenantId=$tenantId"
    Write-Output "  VerifiedId__ClientId=$appId"
    Write-Output ''
}

if (-not $vcSp) {
    Write-Warning 'MANUAL STEP REQUIRED: The Entra Verified ID service principal was not found in this tenant.'
    Write-Warning "In the Entra portal: App registrations > $DisplayName > API permissions > Add a permission"
    Write-Warning '> APIs my organization uses > search ''Verifiable Credentials'''
    Write-Warning "> Add 'VerifiableCredential.Create.All' (Application) > Grant admin consent"
}

Write-Output 'Entra portal link:'
Write-Output "  https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/$appId"
Write-Output ''
