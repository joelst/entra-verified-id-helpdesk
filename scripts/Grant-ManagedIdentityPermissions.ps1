<#
.SYNOPSIS
    Grants Microsoft Graph application permissions to App Service managed identities.

.DESCRIPTION
    After deploying infrastructure with Bicep, run this script to grant the minimum
    required Graph permissions to each App Service's system-assigned managed identity.

    AgentPortal gets: User.Read.All, GroupMember.Read.All (directory search, group checks)
    API gets: User.Read.All, Mail.Send, Chat.Create, Chat.ReadWrite.All (notifications)
    API gets: VerifiableCredential.Create.All (Entra Verified ID presentation requests)

    Prerequisites:
      - Azure CLI installed and logged in
      - Infrastructure already deployed (app services must exist with managed identities)
      - You must be a Global Administrator or Privileged Role Administrator

    This script does not depend on the Key Vault name. If you customized the App Service
    names instead of using the default suffix-based convention, pass them explicitly.

.PARAMETER ResourceGroupName
    Name of the Azure resource group containing the app services.

.PARAMETER Suffix
    Optional suffix used in the default app names (for example, 'gecko-hd' from 'app-agents-gecko-hd').
    If omitted, provide both -AgentPortalAppName and -ApiAppName.

.PARAMETER AgentPortalAppName
    Optional explicit name of the AgentPortal App Service.

.PARAMETER ApiAppName
    Optional explicit name of the Backend API App Service.

.EXAMPLE
    .\scripts\Grant-ManagedIdentityPermissions.ps1 -ResourceGroupName verified-id-ncus -Suffix gecko-hd

.EXAMPLE
    .\scripts\Grant-ManagedIdentityPermissions.ps1 -ResourceGroupName verified-id-ncus -AgentPortalAppName app-agents-gecko-hd -ApiAppName app-api-gecko-hd
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResourceGroupName,

    [string] $Suffix,

    [string] $AgentPortalAppName,

    [string] $ApiAppName
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Step([string]$Message) {
    Write-Information ''
    Write-Information ">> $Message"
}

function Test-AzureCliAvailable {
    $azCommand = Get-Command -Name 'az' -ErrorAction SilentlyContinue
    if (-not $azCommand) {
        Write-Error "Azure CLI ('az') was not found in PATH. Install Azure CLI and run 'az login' first."
        exit 1
    }

    try {
        $null = az version 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw 'Azure CLI returned a non-zero exit code.'
        }
    }
    catch {
        Write-Error 'Azure CLI is installed but could not be executed successfully. Verify the installation and try again.'
        exit 1
    }
}

function Get-AppRoleId([PSCustomObject]$ServicePrincipal, [string]$PermissionName) {
    $role = $ServicePrincipal.appRoles |
        Where-Object { $_.value -eq $PermissionName -and $_.allowedMemberTypes -contains 'Application' }
    if (-not $role) {
        throw "Permission '$PermissionName' not found on '$($ServicePrincipal.appDisplayName)'"
    }
    return $role.id
}

function Grant-AppRole(
    [string]$PrincipalId,
    [string]$PrincipalDisplayName,
    [string]$GraphSpObjectId,
    [string]$AppRoleId,
    [string]$PermissionName
) {
    Write-Information "  Granting $PermissionName to $PrincipalDisplayName ..."

    $body = @{
        principalId = $PrincipalId
        resourceId  = $GraphSpObjectId
        appRoleId   = $AppRoleId
    }

    $bodyJson = $body | ConvertTo-Json -Compress
    $tempFile = [System.IO.Path]::Combine(
        [System.IO.Path]::GetTempPath(),
        "$([System.Guid]::NewGuid()).json"
    )
    $bodyJson | Set-Content $tempFile -Encoding UTF8

    try {
        $uri = "https://graph.microsoft.com/v1.0/servicePrincipals/$GraphSpObjectId/appRoleAssignments"
        az rest --method POST --uri $uri --headers 'Content-Type=application/json' --body "@$tempFile" 2>&1 | Out-Null

        if ($LASTEXITCODE -ne 0) {
            # Check if this is a 409 Conflict (already assigned)
            Write-Information '    Already assigned (or conflict) — continuing'
        }
        else {
            Write-Information '    Granted'
        }
    }
    catch {
        # HTTP 409 Conflict means the permission is already assigned — that's fine
        if ($_.Exception.Message -match '409' -or $_.Exception.Message -match 'Conflict' -or $_.Exception.Message -match 'already exists') {
            Write-Information '    Already assigned — continuing'
        }
        else {
            throw
        }
    }
    finally {
        Remove-Item $tempFile -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------
# Verify Azure CLI availability and login
# ---------------------------------------------------------------------------

Write-Step 'Checking Azure CLI availability'
Test-AzureCliAvailable
Write-Information '  Azure CLI found'

Write-Step 'Checking Azure CLI login'
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Error "Not logged in to Azure CLI. Run 'az login' first."
    exit 1
}
Write-Information "  Signed in as : $($account.user.name)"
Write-Information "  Tenant       : $($account.tenantId)"

# ---------------------------------------------------------------------------
# Look up managed identity principal IDs
# ---------------------------------------------------------------------------

Write-Step 'Looking up managed identity principal IDs'

if ([string]::IsNullOrWhiteSpace($AgentPortalAppName) -or [string]::IsNullOrWhiteSpace($ApiAppName)) {
    if ([string]::IsNullOrWhiteSpace($Suffix)) {
        Write-Error 'Provide either -Suffix or both -AgentPortalAppName and -ApiAppName.'
        exit 1
    }

    if ([string]::IsNullOrWhiteSpace($AgentPortalAppName)) {
        $AgentPortalAppName = "app-agents-$Suffix"
    }

    if ([string]::IsNullOrWhiteSpace($ApiAppName)) {
        $ApiAppName = "app-api-$Suffix"
    }
}

$agentAppName = $AgentPortalAppName
$apiAppName = $ApiAppName

$agentIdentity = az webapp identity show -g $ResourceGroupName -n $agentAppName 2>$null | ConvertFrom-Json
if (-not $agentIdentity -or -not $agentIdentity.principalId) {
    Write-Error "Could not find system-assigned managed identity for '$agentAppName'. Ensure the app exists and has a managed identity enabled."
    exit 1
}
Write-Information "  $agentAppName : $($agentIdentity.principalId)"

$apiIdentity = az webapp identity show -g $ResourceGroupName -n $apiAppName 2>$null | ConvertFrom-Json
if (-not $apiIdentity -or -not $apiIdentity.principalId) {
    Write-Error "Could not find system-assigned managed identity for '$apiAppName'. Ensure the app exists and has a managed identity enabled."
    exit 1
}
Write-Information "  $apiAppName : $($apiIdentity.principalId)"

# ---------------------------------------------------------------------------
# Look up Microsoft Graph service principal
# ---------------------------------------------------------------------------

Write-Step 'Looking up Microsoft Graph service principal'

$GraphAppId = '00000003-0000-0000-c000-000000000000'
$graphSp = az ad sp show --id $GraphAppId 2>$null | ConvertFrom-Json
if (-not $graphSp) {
    Write-Error 'Could not find the Microsoft Graph service principal. Is this a valid Entra tenant?'
    exit 1
}
$graphSpObjectId = $graphSp.id
Write-Information "  Found: $($graphSp.appDisplayName) (object ID: $graphSpObjectId)"

# ---------------------------------------------------------------------------
# Resolve permission IDs
# ---------------------------------------------------------------------------

Write-Step 'Resolving Graph permission IDs'

$agentPermNames = @('User.Read.All', 'GroupMember.Read.All')
$apiPermNames = @('User.Read.All', 'Mail.Send', 'Chat.Create', 'Chat.ReadWrite.All')

# Resolve all unique permission names
$allPermNames = ($agentPermNames + $apiPermNames) | Sort-Object -Unique
$permIdMap = @{}
foreach ($permName in $allPermNames) {
    $permIdMap[$permName] = Get-AppRoleId $graphSp $permName
    Write-Information "  $permName = $($permIdMap[$permName])"
}

# ---------------------------------------------------------------------------
# Grant permissions to AgentPortal managed identity
# ---------------------------------------------------------------------------

Write-Step "Granting Graph permissions to AgentPortal ($agentAppName)"

foreach ($permName in $agentPermNames) {
    Grant-AppRole `
        -PrincipalId $agentIdentity.principalId `
        -PrincipalDisplayName $agentAppName `
        -GraphSpObjectId $graphSpObjectId `
        -AppRoleId $permIdMap[$permName] `
        -PermissionName $permName
}

# ---------------------------------------------------------------------------
# Grant permissions to API managed identity
# ---------------------------------------------------------------------------

Write-Step "Granting Graph permissions to API ($apiAppName)"

foreach ($permName in $apiPermNames) {
    Grant-AppRole `
        -PrincipalId $apiIdentity.principalId `
        -PrincipalDisplayName $apiAppName `
        -GraphSpObjectId $graphSpObjectId `
        -AppRoleId $permIdMap[$permName] `
        -PermissionName $permName
}

# ---------------------------------------------------------------------------
# Grant Verified ID permission to API managed identity
# ---------------------------------------------------------------------------

Write-Step "Granting Verified ID permission to API ($apiAppName)"

$VerifiedIdAppId = '3db474b9-6a0c-4840-96ac-1fceb342124f'
$vcSp = az ad sp show --id $VerifiedIdAppId 2>$null | ConvertFrom-Json
if (-not $vcSp) {
    Write-Warning 'Entra Verified ID service principal not found in this tenant.'
    Write-Warning 'Grant VerifiableCredential.Create.All to the API managed identity manually.'
}
else {
    $vcSpObjectId = $vcSp.id
    $vcRoleId = Get-AppRoleId $vcSp 'VerifiableCredential.Create.All'
    Write-Information "  VerifiableCredential.Create.All = $vcRoleId"

    Grant-AppRole `
        -PrincipalId $apiIdentity.principalId `
        -PrincipalDisplayName $apiAppName `
        -GraphSpObjectId $vcSpObjectId `
        -AppRoleId $vcRoleId `
        -PermissionName 'VerifiableCredential.Create.All'
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

Write-Output ''
Write-Output '======================================================'
Write-Output '  Managed identity permissions granted!'
Write-Output '======================================================'
Write-Output ''
Write-Output "  AgentPortal ($agentAppName):"
Write-Output "    - $($agentPermNames -join ', ')"
Write-Output ''
Write-Output "  API ($apiAppName):"
Write-Output "    - $($apiPermNames -join ', ')"
Write-Output '    - VerifiableCredential.Create.All (Entra Verified ID)'
Write-Output ''
Write-Output '  Note: It may take a few minutes for permissions to propagate.'
Write-Output ''
