<#
.SYNOPSIS
    Creates an Entra app registration for the Verified ID Helpdesk application.

.DESCRIPTION
    This script automates the steps in the "Entra App Registration Setup" section of the README:
      - Creates a single-tenant app registration
      - Adds all required API permissions (Microsoft Graph + Entra Verified ID)
      - Grants admin consent
      - Configures Security Group claims in the token
      - Optionally creates a self-signed certificate in Key Vault and registers it with the app

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
    [string] $LocalDevCertPath   = "$HOME/.entra-vidhelp/$CertName.pem",

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
# Look up service principals for Microsoft Graph and Entra Verified ID
# ---------------------------------------------------------------------------

Write-Step 'Looking up service principals'

# Microsoft Graph app ID is stable across all tenants
$GraphAppId = '00000003-0000-0000-c000-000000000000'

# Microsoft Entra Verified ID (Azure AD Verifiable Credentials)
$VerifiedIdAppId = '3db474b9-6a0c-4840-96ac-1fceb342124f'

$graphSp = az ad sp show --id $GraphAppId 2>$null | ConvertFrom-Json
if (-not $graphSp) {
    Write-Error 'Could not find the Microsoft Graph service principal. Is this a valid Entra tenant?'
    exit 1
}
Write-Information "  Found: $($graphSp.appDisplayName)"

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

$graphPermNames = @('User.Read.All', 'GroupMember.Read.All', 'Mail.Send', 'Chat.Create', 'Chat.ReadWrite.All')
$graphPermIds = $graphPermNames | ForEach-Object { Get-AppRoleId $graphSp $_ }
Write-Information "  Graph permissions resolved: $($graphPermNames -join ', ')"

$requiredAccess = @(
    @{
        resourceAppId  = $GraphAppId
        resourceAccess = @($graphPermIds | ForEach-Object { @{ id = $_; type = 'Role' } })
    }
)

if ($vcSp) {
    $vcPermId = Get-AppRoleId $vcSp 'VerifiableCredential.Create.All'
    Write-Information '  Verified ID permission resolved: VerifiableCredential.Create.All'
    $requiredAccess += @{
        resourceAppId  = $VerifiedIdAppId
        resourceAccess = @(@{ id = $vcPermId; type = 'Role' })
    }
}

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
# Create the service principal so admin-consent can target it
# ---------------------------------------------------------------------------

Write-Step 'Creating service principal'
az ad sp create --id $appId | Out-Null
Write-Information '  Done'

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
# Grant admin consent
# ---------------------------------------------------------------------------

Write-Step 'Granting admin consent'
Write-Information '  Waiting a few seconds for the service principal to propagate...'
Start-Sleep -Seconds 10

az ad app permission admin-consent --id $appId
Write-Information '  Admin consent granted'

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
    Write-Output '-- Next step -----------------------------------------'
    Write-Output '  Deploy Azure infrastructure (Bicep), then run:'
    Write-Output "  .\scripts\Set-AppCertificate.ps1 -KeyVaultName <kv-name> -AppId $appId"
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
