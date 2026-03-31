<#
.SYNOPSIS
    Creates a Key Vault certificate and registers it with an existing Entra app registration.

.DESCRIPTION
    This script handles the certificate half of the app registration setup:
      - Assigns Key Vault Certificates Officer role to the caller (unless -SkipRoleAssignment)
      - Creates a self-signed certificate in Key Vault
      - Registers the certificate (public key only) with the app registration
      - Optionally exports the certificate PFX for local development

    Run this after:
      1. New-AppRegistration.ps1 -SkipCert  (creates the app reg, outputs clientId)
      2. Azure infrastructure deployment     (creates the Key Vault)

    Prerequisites:
      - PowerShell 7+
      - Azure CLI installed and logged in (az login)
      - Key Vault Certificates Officer role on the target vault (script can assign it if you
        have Owner or User Access Administrator on the vault)

.PARAMETER AppId
    Application (client) ID of the existing app registration. Printed by New-AppRegistration.ps1.

.PARAMETER KeyVaultName
    Name of the Azure Key Vault where the certificate will be created.

.PARAMETER CertName
    Name of the certificate in Key Vault. Default: "EntraClientCert"
    Must match the CertName used in New-AppRegistration.ps1 (or the default).

.PARAMETER CertValidityMonths
    Number of months the self-signed certificate is valid for. Default: 24

.PARAMETER DisplayName
    Used as the certificate subject CN. Default: "VerifiedID Helpdesk"

.PARAMETER SkipRoleAssignment
    Skip automatic Key Vault Certificates Officer role assignment. Use if you have already
    assigned the role or prefer to assign it yourself.

.PARAMETER SkipLocalDevCert
    Skip exporting the certificate PFX for local development.

.PARAMETER LocalDevCertPath
    Path where the certificate PFX is saved for local development.
    Default: "$HOME/.entra-vidhelp/<CertName>.pfx"

.EXAMPLE
    # After running New-AppRegistration.ps1 -SkipCert and deploying infrastructure:
    .\Set-AppCertificate.ps1 -KeyVaultName kv-helpdesk-prod -AppId xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx

.EXAMPLE
    # Skip local dev cert export (e.g. on a CI machine)
    .\Set-AppCertificate.ps1 -KeyVaultName kv-helpdesk-prod -AppId <appId> -SkipLocalDevCert
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $AppId,

    [Parameter(Mandatory)]
    [string] $KeyVaultName,

    [string] $CertName           = 'EntraClientCert',
    [int]    $CertValidityMonths = 24,
    [string] $DisplayName        = 'VerifiedID Helpdesk',

    [string] $LocalDevCertPath   = "$HOME/.entra-vidhelp/$CertName.pfx",

    [switch] $SkipRoleAssignment,
    [switch] $SkipLocalDevCert
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Information ''
    Write-Information ">> $Message"
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
# Verify app registration exists
# ---------------------------------------------------------------------------

Write-Step "Verifying app registration '$AppId'"
$app = az ad app show --id $AppId 2>$null | ConvertFrom-Json
if (-not $app) {
    Write-Error "App registration '$AppId' not found. Ensure -AppId is correct and you are in the right tenant."
    exit 1
}
Write-Information "  Found: $($app.displayName)"

# ---------------------------------------------------------------------------
# Ensure caller has Key Vault Certificates Officer on the target vault
# ---------------------------------------------------------------------------

Write-Step "Checking Key Vault permissions on '$KeyVaultName'"

$kvId = az keyvault show --name $KeyVaultName --query id -o tsv 2>$null
if (-not $kvId) {
    Write-Error "Key Vault '$KeyVaultName' not found or you do not have access. Ensure it exists and you are logged in to the correct subscription."
    exit 1
}
Write-Information "  Key Vault ID: $kvId"

$userId = az ad signed-in-user show --query id -o tsv 2>$null
if (-not $userId) {
    Write-Error "Could not determine the signed-in user's object ID."
    exit 1
}

$certOfficerRoleName = 'Key Vault Certificates Officer'
$existingAssignment = az role assignment list --assignee $userId --role $certOfficerRoleName --scope $kvId --query "[0].id" -o tsv 2>$null

if ($existingAssignment) {
    Write-Information "  Role '$certOfficerRoleName' already assigned."
}
elseif ($SkipRoleAssignment) {
    Write-Warning "Role '$certOfficerRoleName' not found on '$KeyVaultName' and -SkipRoleAssignment was specified. The certificate creation may fail."
}
else {
    Write-Information "  Role '$certOfficerRoleName' not assigned — attempting to assign it now..."
    az role assignment create --role $certOfficerRoleName --assignee $userId --scope $kvId --output none
    if ($LASTEXITCODE -ne 0) {
        Write-Error @"
Failed to assign '$certOfficerRoleName' on '$KeyVaultName'.
You may not have Owner or User Access Administrator on the vault.

Assign the role manually, then re-run with -SkipRoleAssignment:
  az role assignment create --role "$certOfficerRoleName" ``
    --assignee $userId ``
    --scope $kvId
"@
        exit 1
    }
    Write-Information "  Role assigned. Waiting 30 seconds for RBAC to propagate..."
    Start-Sleep -Seconds 30
}

# ---------------------------------------------------------------------------
# Create self-signed certificate in Key Vault
# ---------------------------------------------------------------------------

Write-Step "Creating certificate '$CertName' in Key Vault '$KeyVaultName' (valid $CertValidityMonths months)"

# Build a certificate policy: self-signed, exportable private key, RSA 2048.
# exportable must be true: Microsoft.Identity.Web reads the full cert (including private key)
# via the Key Vault Secrets API, which only returns the private key for exportable certificates.
$certPolicy = @{
    issuerParameters = @{ name = 'Self' }
    keyProperties    = @{
        exportable = $true
        keyType    = 'RSA'
        keySize    = 2048
        reuseKey   = $false
    }
    secretProperties = @{ contentType = 'application/x-pkcs12' }
    x509CertificateProperties = @{
        subject          = "CN=$DisplayName"
        validityInMonths = $CertValidityMonths
        keyUsage         = @('digitalSignature')
        ekus             = @('1.3.6.1.5.5.7.3.2')   # clientAuth
    }
} | ConvertTo-Json -Depth 10 -Compress

$policyFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "$([System.Guid]::NewGuid()).json")
$certPolicy | Set-Content $policyFile -Encoding UTF8

az keyvault certificate create --vault-name $KeyVaultName --name $CertName --policy "@$policyFile" --output none
Remove-Item $policyFile -ErrorAction SilentlyContinue

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create certificate in Key Vault '$KeyVaultName'. Ensure you have the 'Key Vault Certificates Officer' role."
    exit 1
}
Write-Information "  Certificate created in Key Vault."

# ---------------------------------------------------------------------------
# Register certificate public key with the app registration
# ---------------------------------------------------------------------------

Write-Step "Registering certificate with app registration '$AppId'"

$certFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "$([System.Guid]::NewGuid()).cer")
az keyvault certificate download --vault-name $KeyVaultName --name $CertName --file $certFile --encoding DER --output none

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to download certificate public key from Key Vault."
    exit 1
}

az ad app credential reset --id $AppId --cert "@$certFile" --append --display-name $CertName --output none
Remove-Item $certFile -ErrorAction SilentlyContinue

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to register certificate with app registration '$AppId'."
    exit 1
}
Write-Information "  Certificate registered with app registration."

# ---------------------------------------------------------------------------
# Export certificate PFX for local development
# ---------------------------------------------------------------------------

if (-not $SkipLocalDevCert -and $LocalDevCertPath) {
    # Adjust extension: PKCS#12 certs export as .pfx, not .pem
    $LocalDevCertPath = [System.IO.Path]::ChangeExtension($LocalDevCertPath, '.pfx')

    Write-Step "Exporting certificate PFX for local development"

    $certDir = Split-Path $LocalDevCertPath -Parent
    if ($certDir -and -not (Test-Path $certDir)) {
        New-Item -ItemType Directory -Path $certDir -Force | Out-Null
    }

    # The KV Secrets endpoint returns the full PKCS#12 (base64) for exportable certs.
    $pfxBase64 = az keyvault secret show `
        --vault-name $KeyVaultName `
        --name $CertName `
        --query value -o tsv 2>$null

    if ($LASTEXITCODE -ne 0 -or -not $pfxBase64) {
        Write-Warning "Could not download certificate from Key Vault. Export it manually with:"
        Write-Warning "  az keyvault secret show --vault-name $KeyVaultName --name $CertName --query value -o tsv"
    }
    else {
        [System.IO.File]::WriteAllBytes($LocalDevCertPath, [System.Convert]::FromBase64String($pfxBase64))
        Write-Information "  Certificate PFX saved to: $LocalDevCertPath"
        Write-Warning "  IMPORTANT: This file contains the private key. Keep it secure and do not commit it to source control."

        $localDevProjects = @(
            'src/VerifiedIdHelpdesk.Api',
            'src/VerifiedIdHelpdesk.AgentPortal'
        )

        $secretsSet = $false
        foreach ($proj in $localDevProjects) {
            if (Test-Path $proj) {
                dotnet user-secrets --project $proj set "AzureAd:ClientCertificates:0:SourceType" "Path" | Out-Null
                dotnet user-secrets --project $proj set "AzureAd:ClientCertificates:0:CertificateDiskPath" $LocalDevCertPath | Out-Null
                if ($LASTEXITCODE -eq 0) {
                    Write-Information "  user-secrets configured for: $proj"
                    $secretsSet = $true
                }
            }
        }

        if (-not $secretsSet) {
            Write-Information '  Project directories not found — skipping user-secrets (not needed for Azure deployment).'
        }
    }
}

$tenantId = $account.tenantId

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

Write-Output ''
Write-Output '======================================================'
Write-Output '  Certificate setup complete!'
Write-Output '======================================================'
Write-Output ''
Write-Output '-- appsettings.json ----------------------------------'
Write-Output "  AzureAd:TenantId                                 = $tenantId"
Write-Output "  AzureAd:ClientId                                 = $AppId"
Write-Output "  AzureAd:ClientCertificates:0:SourceType          = KeyVault"
Write-Output "  AzureAd:ClientCertificates:0:KeyVaultUrl         = https://$KeyVaultName.vault.azure.net/"
Write-Output "  AzureAd:ClientCertificates:0:KeyVaultCertificateName = $CertName"
Write-Output "  VerifiedId:TenantId                              = $tenantId"
Write-Output "  VerifiedId:ClientId                              = $AppId"
Write-Output ''
Write-Output '-- App Service Application Settings (env vars) -------'
Write-Output "  AzureAd__TenantId=$tenantId"
Write-Output "  AzureAd__ClientId=$AppId"
Write-Output "  AzureAd__ClientCertificates__0__SourceType=KeyVault"
Write-Output "  AzureAd__ClientCertificates__0__KeyVaultUrl=https://$KeyVaultName.vault.azure.net/"
Write-Output "  AzureAd__ClientCertificates__0__KeyVaultCertificateName=$CertName"
Write-Output "  VerifiedId__TenantId=$tenantId"
Write-Output "  VerifiedId__ClientId=$AppId"
Write-Output ''
Write-Output 'Entra portal link:'
Write-Output "  https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/$AppId"
Write-Output ''
