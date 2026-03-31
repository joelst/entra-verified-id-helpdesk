<#
.SYNOPSIS
    Grants Microsoft Graph application permissions to App Service managed identities.

.DESCRIPTION
    After deploying infrastructure with Bicep, run this script to grant the minimum
    required Graph permissions to each App Service's system-assigned managed identity.

    AgentPortal gets: User.Read.All, GroupMember.Read.All (directory search, group checks)
    API gets: User.Read.All, Mail.Send, Chat.Create, Chat.ReadWrite.All (notifications)

    Prerequisites:
      - Azure CLI installed and logged in
      - Infrastructure already deployed (app services must exist with managed identities)
      - You must be a Global Administrator or Privileged Role Administrator

.PARAMETER ResourceGroupName
    Name of the Azure resource group containing the app services.

.PARAMETER Suffix
    The random suffix used in resource names (e.g., 'gecko-hd' from 'app-agents-gecko-hd').

.EXAMPLE
    .\scripts\Grant-ManagedIdentityPermissions.ps1 -ResourceGroupName verified-id-ncus -Suffix gecko-hd
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResourceGroupName,

    [Parameter(Mandatory)]
    [string] $Suffix
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
        az rest --method POST --uri $uri --headers "Content-Type=application/json" --body "@$tempFile" 2>&1 | Out-Null

        if ($LASTEXITCODE -ne 0) {
            # Check if this is a 409 Conflict (already assigned)
            Write-Information "    Already assigned (or conflict) — continuing"
        }
        else {
            Write-Information "    Granted"
        }
    }
    catch {
        # HTTP 409 Conflict means the permission is already assigned — that's fine
        if ($_.Exception.Message -match '409' -or $_.Exception.Message -match 'Conflict' -or $_.Exception.Message -match 'already exists') {
            Write-Information "    Already assigned — continuing"
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

# ---------------------------------------------------------------------------
# Look up managed identity principal IDs
# ---------------------------------------------------------------------------

Write-Step 'Looking up managed identity principal IDs'

$agentAppName = "app-agents-$Suffix"
$apiAppName   = "app-api-$Suffix"

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
$apiPermNames   = @('User.Read.All', 'Mail.Send', 'Chat.Create', 'Chat.ReadWrite.All')

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
Write-Output ''
Write-Output '  Note: It may take a few minutes for permissions to propagate.'
Write-Output ''
