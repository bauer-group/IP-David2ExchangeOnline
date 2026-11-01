# SPDX-License-Identifier: MIT
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [string]$AppName = "david2exchange-migration",
    [string]$CertificateThumbprint = "",
    [string]$AccessPolicyGroup = "",
    [string]$OutputJson = "",
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"

function Write-Result {
    param([hashtable]$Result)
    if (-not [string]::IsNullOrWhiteSpace($OutputJson)) {
        $Result | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $OutputJson -Encoding UTF8
    }
    else {
        $Result | ConvertTo-Json -Depth 8 -Compress
    }
}

function Ensure-Module {
    param([string]$Name)
    if (-not (Get-Module -ListAvailable -Name $Name)) {
        Write-Host "Installiere Modul $Name ..."
        Install-Module -Name $Name -Scope CurrentUser -Force -AllowClobber
    }
    Import-Module $Name -ErrorAction Stop
}

Ensure-Module "Microsoft.Graph.Applications"
Ensure-Module "Microsoft.Graph.Identity.SignIns"
Ensure-Module "ExchangeOnlineManagement"

$exchangeResourceId = "00000002-0000-0ff1-ce00-000000000000"
$fullAccessRoleId = "dc890d15-9560-4a4c-9b7f-a736ec74ec40"

Write-Host "Verbinde Microsoft Graph ..."
Connect-MgGraph `
    -Scopes "Application.ReadWrite.All", "AppRoleAssignment.ReadWrite.All", "Directory.Read.All" `
    -UseDeviceAuthentication `
    -NoWelcome

$graphContext = Get-MgContext
$tenantId = $graphContext.TenantId

$app = Get-MgApplication -Filter "displayName eq '$AppName'" -ConsistencyLevel eventual | Select-Object -First 1

if ($CheckOnly) {
    if (-not $app) {
        Write-Result @{ ok = $false; tenantId = $tenantId; message = "App '$AppName' nicht gefunden." }
        exit 0
    }

    Connect-ExchangeOnline -Device -ShowBanner:$false
    $config = Get-OrganizationConfig -RetrieveEwsOperationAccessPolicy
    $allowed = @($config.EwsAllowedAppIDs)
    Write-Result @{
        ok = $true
        checkOnly = $true
        tenantId = $tenantId
        clientId = $app.AppId
        certThumbprint = $CertificateThumbprint
        ewsEnabled = [bool]$config.EwsEnabled
        appIdInAllowlist = ($allowed -contains $app.AppId)
        allowlistCount = $allowed.Count
    }
    exit 0
}

function New-DavidCertificate {
    param([string]$Subject, [int]$Months = 12)

    # New-SelfSignedCertificate kann auf manchen Workstations in der
    # Schlüsselerzeugung hängen. Deshalb mit Timeout und certreq-Fallback.
    $job = Start-Job -ScriptBlock {
        param($subject, $months)
        New-SelfSignedCertificate `
            -Subject $subject `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -KeyExportPolicy NonExportable `
            -NotAfter (Get-Date).AddMonths($months)
    } -ArgumentList $Subject, $Months

    if (Wait-Job $job -Timeout 90) {
        $created = Receive-Job $job
        Remove-Job $job -Force
        if ($created) {
            return $created
        }
    }
    else {
        Stop-Job $job -ErrorAction SilentlyContinue
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        Write-Host "New-SelfSignedCertificate antwortet nicht; nutze certreq-Fallback ..."
    }

    $temp = Join-Path $env:TEMP ("david2exchange-cert-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $inf = @'
[Version]
Signature="$Windows NT$"

[NewRequest]
Subject = "{0}"
KeySpec = 1
KeyLength = 2048
Exportable = FALSE
MachineKeySet = FALSE
ProviderName = "Microsoft RSA SChannel Cryptographic Provider"
ProviderType = 1
RequestType = Cert
KeyUsage = 0xA0
'@ -f $Subject

    $infPath = Join-Path $temp "request.inf"
    $cerPath = Join-Path $temp "certificate.cer"
    Set-Content -LiteralPath $infPath -Value $inf -Encoding ASCII

    $output = & certreq -new $infPath $cerPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Zertifikatserstellung fehlgeschlagen (certreq): $output"
    }

    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if (-not $certificate) {
        throw "certreq meldete Erfolg, aber kein Zertifikat mit privatem Schlüssel gefunden."
    }

    return $certificate
}

if (-not $CertificateThumbprint) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq "CN=$AppName" -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Select-Object -First 1

    if (-not $certificate) {
        Write-Host "Erzeuge selbstsigniertes Zertifikat ..."
        $certificate = New-DavidCertificate -Subject "CN=$AppName" -Months 12
    }

    $CertificateThumbprint = $certificate.Thumbprint
}
else {
    $certificate = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint"
}

if (-not $app) {
    Write-Host "Lege App-Registrierung '$AppName' an ..."
    $app = New-MgApplication -DisplayName $AppName -SignInAudience "AzureADMyOrg"
}
else {
    Write-Host "Aktualisiere vorhandene App '$AppName' ..."
}

$requiredAccess = @(
    @{
        resourceAppId = $exchangeResourceId
        resourceAccess = @(
            @{ id = $fullAccessRoleId; type = "Role" }
        )
    }
)

$keyCredential = @{
    type = "AsymmetricX509Cert"
    usage = "Verify"
    key = $certificate.RawData
    displayName = "$AppName-$($certificate.Thumbprint.Substring(0, 8))"
}

Update-MgApplication `
    -ApplicationId $app.Id `
    -RequiredResourceAccess $requiredAccess `
    -KeyCredentials @($keyCredential) | Out-Null

$servicePrincipal = Get-MgServicePrincipal -Filter "appId eq '$($app.AppId)'" | Select-Object -First 1
if (-not $servicePrincipal) {
    Write-Host "Lege Service Principal an ..."
    $servicePrincipal = New-MgServicePrincipal -AppId $app.AppId
}

$exchangeServicePrincipal = Get-MgServicePrincipal -Filter "appId eq '$exchangeResourceId'" | Select-Object -First 1
if (-not $exchangeServicePrincipal) {
    throw "Exchange-Online-Service-Principal nicht gefunden."
}

$existingAssignment = Get-MgServicePrincipalAppRoleAssignedTo `
    -ServicePrincipalId $exchangeServicePrincipal.Id |
    Where-Object { $_.PrincipalId -eq $servicePrincipal.Id -and $_.AppRoleId -eq $fullAccessRoleId } |
    Select-Object -First 1

if (-not $existingAssignment) {
    Write-Host "Erteile Admin-Consent (full_access_as_app) ..."
    New-MgServicePrincipalAppRoleAssignment `
        -ServicePrincipalId $exchangeServicePrincipal.Id `
        -PrincipalId $servicePrincipal.Id `
        -ResourceId $exchangeServicePrincipal.Id `
        -AppRoleId $fullAccessRoleId | Out-Null
}

Write-Host "Verbinde Exchange Online ..."
Connect-ExchangeOnline -Device -ShowBanner:$false

$organization = Get-OrganizationConfig -RetrieveEwsOperationAccessPolicy
$allowlistBefore = @($organization.EwsAllowedAppIDs | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$backupPath = Join-Path (Split-Path -Parent $ConfigPath) ("setup-backup-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".json")
[pscustomobject]@{
    ewsEnabled = $organization.EwsEnabled
    ewsAllowedAppIDs = $allowlistBefore
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $backupPath -Encoding UTF8

$allowlistAfter = @($allowlistBefore)
if ($allowlistAfter -notcontains $app.AppId) {
    $allowlistAfter += $app.AppId
}

Write-Host "Aktualisiere EWS-Allowlist ..."
Set-OrganizationConfig -EwsEnabled $true -EwsAllowedAppIDs ($allowlistAfter -join ",")
Start-Sleep -Seconds 3
$verified = Get-OrganizationConfig -RetrieveEwsOperationAccessPolicy

if ($AccessPolicyGroup) {
    Write-Host "Setze Application Access Policy auf '$AccessPolicyGroup' ..."
    $existingPolicy = Get-ApplicationAccessPolicy -ErrorAction SilentlyContinue |
        Where-Object { $_.AppId -eq $app.AppId } |
        Select-Object -First 1

    if (-not $existingPolicy) {
        New-ApplicationAccessPolicy `
            -AppId $app.AppId `
            -PolicyScopeGroupId $AccessPolicyGroup `
            -AccessRight RestrictAccess `
            -Description "david2exchange migration" | Out-Null
    }
}

Write-Result @{
    ok = $true
    tenantId = $tenantId
    clientId = $app.AppId
    certThumbprint = $CertificateThumbprint
    appObjectId = $app.Id
    servicePrincipalId = $servicePrincipal.Id
    allowlistBefore = $allowlistBefore
    allowlistAfter = @($verified.EwsAllowedAppIDs)
    ewsEnabled = [bool]$verified.EwsEnabled
    backupPath = $backupPath
}
