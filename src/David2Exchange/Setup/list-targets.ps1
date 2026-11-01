# SPDX-License-Identifier: MIT
[CmdletBinding()]
param(
    [string]$OutputJson = ""
)

$ErrorActionPreference = "Stop"

if (-not (Get-Module -ListAvailable -Name ExchangeOnlineManagement)) {
    Install-Module -Name ExchangeOnlineManagement -Scope CurrentUser -Force -AllowClobber
}

Import-Module ExchangeOnlineManagement -ErrorAction Stop
Connect-ExchangeOnline -Device -ShowBanner:$false

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

$json = @($result) | ConvertTo-Json -Depth 3 -AsArray
if (-not [string]::IsNullOrWhiteSpace($OutputJson)) {
    $json | Set-Content -LiteralPath $OutputJson -Encoding UTF8
}
else {
    $json
}
