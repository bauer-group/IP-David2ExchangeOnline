# SPDX-License-Identifier: MIT
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [string]$AppName = "david2exchange-migration",
    [string]$CertificateThumbprint = "",
    [string]$OutputJson = ""
)

$ErrorActionPreference = "Stop"

function Ensure-Module {
    param([string]$Name)
    if (-not (Get-Module -ListAvailable -Name $Name)) {
        Install-Module -Name $Name -Scope CurrentUser -Force -AllowClobber
    }
    Import-Module $Name -ErrorAction Stop
}

Ensure-Module "Microsoft.Graph.Applications"
Ensure-Module "ExchangeOnlineManagement"

Write-Host "Verbinde Microsoft Graph ..."
Connect-MgGraph `
    -Scopes "Application.ReadWrite.All", "AppRoleAssignment.ReadWrite.All", "Directory.Read.All" `
    -UseDeviceAuthentication `
    -NoWelcome

$app = Get-MgApplication -Filter "displayName eq '$AppName'" -ConsistencyLevel eventual | Select-Object -First 1
if (-not $app) {
    Write-Host "App '$AppName' nicht gefunden; nichts zu entfernen."
}

Write-Host "Verbinde Exchange Online ..."
Connect-ExchangeOnline -Device -ShowBanner:$false

$organization = Get-OrganizationConfig -RetrieveEwsOperationAccessPolicy
$allowlistBefore = @($organization.EwsAllowedAppIDs | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$allowlistAfter = @($allowlistBefore)
if ($app -and $allowlistAfter -contains $app.AppId) {
    $allowlistAfter = @($allowlistAfter | Where-Object { $_ -ne $app.AppId })
    Set-OrganizationConfig -EwsAllowedAppIDs ($allowlistAfter -join ",")
}

if ($app) {
    $policy = Get-ApplicationAccessPolicy -ErrorAction SilentlyContinue |
        Where-Object { $_.AppId -eq $app.AppId } |
        Select-Object -First 1
    if ($policy) {
        Remove-ApplicationAccessPolicy -Identity $policy.Identity -Confirm:$false
    }

    $servicePrincipal = Get-MgServicePrincipal -Filter "appId eq '$($app.AppId)'" | Select-Object -First 1
    if ($servicePrincipal) {
        Remove-MgServicePrincipal -ServicePrincipalId $servicePrincipal.Id
    }

    Remove-MgApplication -ApplicationId $app.Id
}

if ($CertificateThumbprint) {
    $path = "Cert:\CurrentUser\My\$CertificateThumbprint"
    if (Test-Path $path) {
        Remove-Item -Path $path -Force
    }
}

$result = [pscustomobject]@{
    ok = $true
    removedAppId = if ($app) { $app.AppId } else { $null }
    allowlistBefore = $allowlistBefore
    allowlistAfter = @($allowlistAfter)
    ewsEnabled = [bool]$organization.EwsEnabled
}

if (-not [string]::IsNullOrWhiteSpace($OutputJson)) {
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputJson -Encoding UTF8
}
else {
    $result | ConvertTo-Json -Depth 6 -Compress
}
