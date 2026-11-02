# SPDX-License-Identifier: MIT
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InputJson,
    [string]$AdminUpn = "",
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

if (-not (Get-Module -ListAvailable -Name ExchangeOnlineManagement)) {
    Install-Module -Name ExchangeOnlineManagement -Scope CurrentUser -Force -AllowClobber
}

Import-Module ExchangeOnlineManagement -ErrorAction Stop
Connect-ExchangeOnline -Device -ShowBanner:$false

$targets = Get-Content -LiteralPath $InputJson -Raw | ConvertFrom-Json
$created = 0
$permissions = 0
$skipped = 0

foreach ($target in $targets) {
    $upn = [string]$target.Upn
    if ([string]::IsNullOrWhiteSpace($upn)) {
        continue
    }

    $mailbox = Get-Mailbox -Identity $upn -ErrorAction SilentlyContinue
    if (-not $mailbox) {
        if (-not $target.CreateIfMissing) {
            Write-Host "SKIP  $upn (fehlt, createIfMissing=false)"
            $skipped++
            continue
        }

        if ([string]$target.Type -eq "shared") {
            $displayName = if ($target.DisplayName) { $target.DisplayName } else { $upn }
            $alias = if ($target.Alias) { $target.Alias } else { ($upn -split "@")[0] }
            Write-Host "NEW   Shared Mailbox $upn"
            New-Mailbox `
                -Shared `
                -Name $displayName `
                -DisplayName $displayName `
                -Alias $alias `
                -PrimarySmtpAddress $upn `
                -MicrosoftOnlineServicesID $upn `
                -WhatIf:$WhatIf
            $created++
        }
        else {
            Write-Warning "User-Mailbox $upn fehlt; bitte lizenzieren/anlegen. Kein automatisches New-Mailbox für User."
            $skipped++
            continue
        }
    }

    if ($mailbox -and
        [string]$target.Type -eq "shared" -and
        -not [string]::IsNullOrWhiteSpace($mailbox.UserPrincipalName) -and
        $mailbox.UserPrincipalName -ne $upn) {
        Write-Warning "Shared Mailbox $upn hat UPN '$($mailbox.UserPrincipalName)'. " +
            "Der Anmeldename muss manuell auf $upn geändert werden."
    }

    if ($target.Aliases -and @($target.Aliases).Count -gt 0) {
        Write-Host "ALIAS $upn += $($target.Aliases -join ', ')"
        Set-Mailbox -Identity $upn -EmailAddresses @{ Add = @($target.Aliases) } -WhatIf:$WhatIf
    }

    if (-not [string]::IsNullOrWhiteSpace($AdminUpn)) {
        Write-Host "PERM  $upn -> $AdminUpn FullAccess"
        Add-MailboxPermission `
            -Identity $upn `
            -User $AdminUpn `
            -AccessRights FullAccess `
            -InheritanceType All `
            -AutoMapping:$false `
            -WhatIf:$WhatIf
        $permissions++
    }
}

Write-Host "DONE created=$created permissions=$permissions skipped=$skipped whatIf=$($WhatIf.IsPresent)"
