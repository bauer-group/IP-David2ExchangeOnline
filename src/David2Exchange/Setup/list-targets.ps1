# SPDX-License-Identifier: MIT
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

if (-not (Get-Module -ListAvailable -Name ExchangeOnlineManagement)) {
    Install-Module -Name ExchangeOnlineManagement -Scope CurrentUser -Force -AllowClobber
}

Import-Module ExchangeOnlineManagement -ErrorAction Stop
Connect-ExchangeOnline -ShowBanner:$false

try {
    $mailboxes = Get-EXOMailbox -ResultSize Unlimited
}
catch {
    $mailboxes = Get-Mailbox -ResultSize Unlimited
}

$result = $mailboxes |
    Where-Object { $_.RecipientTypeDetails -in @("UserMailbox", "SharedMailbox") } |
    Select-Object DisplayName, PrimarySmtpAddress, RecipientTypeDetails, EmailAddresses |
    Sort-Object PrimarySmtpAddress

@($result) | ConvertTo-Json -Depth 3 -AsArray
