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
$pendingSignIn = [System.Collections.ArrayList]::new()
$failedSignIn = [System.Collections.ArrayList]::new()
$sentCopies = 0
$failedSentCopy = 0

# Benutzername = Mailadresse = UPN. In Exchange Online ersetzt -MicrosoftOnlineServicesID den UPN.
# Bis die Änderung angekommen ist, warnt Exchange über die Abweichung; geprüft wird deshalb am Ende.
function Set-SignInName([string]$Address) {
    try {
        Set-Mailbox -Identity $Address -MicrosoftOnlineServicesID $Address -WarningAction SilentlyContinue -WhatIf:$WhatIf
        if (-not $WhatIf) {
            [void]$pendingSignIn.Add($Address)
        }
    }
    catch {
        [void]$failedSignIn.Add($Address)
        Write-Warning "Anmeldename von $Address nicht gesetzt: $($_.Exception.Message)"
    }
}

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
            $null = New-Mailbox `
                -Shared `
                -Name $displayName `
                -DisplayName $displayName `
                -Alias $alias `
                -PrimarySmtpAddress $upn `
                -WhatIf:$WhatIf
            $created++

            # Im Probelauf existiert das Postfach nicht; die Folgeschritte fänden es nicht.
            if ($WhatIf) {
                continue
            }

            # -MicrosoftOnlineServicesID gehört nicht zum Parametersatz von New-Mailbox -Shared.
            Set-SignInName $upn
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
        # Get-Mailbox findet auch Aliasse; der Anmeldename soll aber die Hauptadresse sein.
        if ([string]$mailbox.PrimarySmtpAddress -eq $upn) {
            Write-Host "UPN   $upn (bisher $($mailbox.UserPrincipalName))"
            Set-SignInName $upn
        }
        else {
            Write-Warning "$upn ist nur ein Alias von $($mailbox.PrimarySmtpAddress); der Anmeldename bleibt unverändert."
        }
    }

    # Was Mitglieder als oder im Auftrag dieses Postfachs senden, liegt auch in seinen Gesendeten Elementen.
    if ([string]$target.Type -eq "shared" -and
        (-not $mailbox -or -not $mailbox.MessageCopyForSentAsEnabled -or -not $mailbox.MessageCopyForSendOnBehalfEnabled)) {
        try {
            Set-Mailbox -Identity $upn -MessageCopyForSentAsEnabled $true -MessageCopyForSendOnBehalfEnabled $true -WhatIf:$WhatIf
            Write-Host "SENT  $upn kopiert gesendete Elemente"
            $sentCopies++
        }
        catch {
            $failedSentCopy++
            Write-Warning "Gesendete Elemente für $upn nicht aktiviert: $($_.Exception.Message)"
        }
    }

    if ($target.Aliases -and @($target.Aliases).Count -gt 0) {
        Write-Host "ALIAS $upn += $($target.Aliases -join ', ')"
        Set-Mailbox -Identity $upn -EmailAddresses @{ Add = @($target.Aliases) } -WhatIf:$WhatIf
    }

    if (-not [string]::IsNullOrWhiteSpace($AdminUpn)) {
        Write-Host "PERM  $upn -> $AdminUpn FullAccess"
        $null = Add-MailboxPermission `
            -Identity $upn `
            -User $AdminUpn `
            -AccessRights FullAccess `
            -InheritanceType All `
            -AutoMapping:$false `
            -WhatIf:$WhatIf
        $permissions++
    }
}

# Erst das Zurücklesen zeigt, ob Benutzername, Mailadresse und UPN wirklich übereinstimmen.
for ($attempt = 0; $pendingSignIn.Count -gt 0 -and $attempt -lt 12; $attempt++) {
    foreach ($address in @($pendingSignIn)) {
        if ((Get-Mailbox -Identity $address -ErrorAction SilentlyContinue).UserPrincipalName -eq $address) {
            Write-Host "UPN   $address bestätigt"
            $pendingSignIn.Remove($address)
        }
    }

    if ($pendingSignIn.Count -gt 0) {
        Start-Sleep -Seconds 15
    }
}

foreach ($address in $pendingSignIn) {
    Write-Warning "Anmeldename von $address noch nicht bestätigt. Den Befehl später erneut ausführen."
}

$signInOpen = $pendingSignIn.Count + $failedSignIn.Count
Write-Host "DONE created=$created permissions=$permissions skipped=$skipped signInOpen=$signInOpen sentCopies=$sentCopies sentCopyOpen=$failedSentCopy whatIf=$($WhatIf.IsPresent)"
