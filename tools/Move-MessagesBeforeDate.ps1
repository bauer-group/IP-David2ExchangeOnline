# SPDX-License-Identifier: MIT
#Requires -Version 7.0
<#
.SYNOPSIS
  Verschiebt alle Nachrichten VOR einem Stichtag aus einem Ordner (nur dieser Ordner, keine Unterordner)
  in einen Zielordner – serverseitig über Microsoft Graph, PARALLEL mit bis zu 4 gleichzeitigen Anfragen
  (das ist das Graph-Maximum pro Postfach).
  Dokumentation: docs/TOOLS.md

.BEISPIELE
  # 1. Nur zählen (ändert nichts)
  .\Move-MessagesBeforeDate.ps1 -Source inbox -TargetPath "Posteingang/[BGI] BAUER GROUP IT (DE)" -Before 2026-01-01 -CountOnly

  # 2. Ausführen
  .\Move-MessagesBeforeDate.ps1 -Source inbox -TargetPath "Posteingang/[BGI] BAUER GROUP IT (DE)" -Before 2026-01-01

  # Gesendete Elemente (anderer Stichtag, Zielpfad anpassen)
  .\Move-MessagesBeforeDate.ps1 -Source sentitems -TargetPath "Gesendete Elemente/[BGI] BAUER GROUP IT (DE)" -Before 2025-07-01

.PARAMETER Source
  Bekannter Ordnername (inbox, sentitems, deleteditems, archive, drafts, junkemail) ODER ein Pfad wie "Posteingang/Irgendwas".

.PARAMETER Before
  Stichtag (lokale Zeit, 00:00 Uhr). Verschoben wird alles, was STRENG VOR diesem Zeitpunkt liegt.

.PARAMETER DateField
  receivedDateTime oder sentDateTime. Standard: sentDateTime bei sentitems, sonst receivedDateTime.

.PARAMETER ThrottleLimit
  Gleichzeitige Anfragen (1–4). Graph erlaubt pro App und Postfach maximal 4 gleichzeitig
  und 10.000 Anfragen je 10 Minuten (~16 Mails/s Obergrenze).

.PARAMETER Mailbox
  Postfach (UPN/SMTP). Leer = Postfach des angemeldeten Benutzers. Ein fremdes Postfach braucht
  Vollzugriff darauf; das Skript fordert dann zusätzlich Mail.ReadWrite.Shared an.

.HINWEISE
  - PowerShell 7 erforderlich. In einem eigenen Fenster OHNE geladenes ExchangeOnlineManagement-Modul ausführen.
  - Abbrechen (Strg+C) und neu starten ist jederzeit möglich – es wird nur verschoben, was noch im Quellordner liegt.
  - Der Zielordner muss existieren.
#>
[CmdletBinding()]
param(
    [string]$Mailbox,
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$TargetPath,
    [Parameter(Mandatory)][datetime]$Before,
    [ValidateSet('receivedDateTime', 'sentDateTime')][string]$DateField,
    [ValidateRange(1, 4)][int]$ThrottleLimit = 4,
    [ValidateRange(50, 1000)][int]$PageSize = 400,
    [switch]$CountOnly
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Authentication)) {
    Install-Module Microsoft.Graph.Authentication -Scope CurrentUser -Force
}
Import-Module Microsoft.Graph.Authentication
$scopes = @('Mail.ReadWrite')
if ($Mailbox) { $scopes += 'Mail.ReadWrite.Shared' }
Connect-MgGraph -Scopes $scopes -NoWelcome
$ctx = Get-MgContext
Write-Host ("Konto  : {0}  |  Postfach: {1}" -f $ctx.Account, $(if ($Mailbox) { $Mailbox } else { $ctx.Account })) -ForegroundColor Cyan

$api = "https://graph.microsoft.com/v1.0"
$mbx = if ($Mailbox) { "/users/$Mailbox" } else { '/me' }

function Invoke-Graph {
    param([string]$Method = 'GET', [string]$Uri, $Body, [hashtable]$Headers, [switch]$Raw)
    for ($try = 1; $try -le 10; $try++) {
        try {
            $p = @{ Method = $Method; Uri = $Uri }
            if ($Headers) { $p.Headers = $Headers }
            if ($Raw) { $p.OutputType = 'HttpResponseMessage' }
            if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 10); $p.ContentType = 'application/json' }
            return Invoke-MgGraphRequest @p
        }
        catch {
            # 429 und 5xx am Statuscode erkennen; ohne Antwort (Netzwerk, Zeitüberschreitung) am Fehlertext
            $code = 0
            try { $code = [int]$_.Exception.Response.StatusCode } catch { }
            $isTransient = $code -eq 429 -or $code -ge 500 -or ($code -eq 0 -and
                "$_ $($_.Exception.InnerException)" -match 'TooManyRequests|429|ServiceUnavailable|GatewayTimeout|timed out|Timeout|transport|connection|SocketException|HttpRequestException')
            if ($isTransient) {
                $wait = [math]::Min(120, 10 * $try)
                Write-Warning "Drosselung/Timeout – warte $wait s (Versuch $try)"
                Start-Sleep -Seconds $wait
                continue
            }
            throw
        }
    }
    throw "Graph-Aufruf nach mehreren Versuchen fehlgeschlagen: $Uri"
}

# Zugriffstoken aus der Graph-SDK-Sitzung holen (SDK erneuert es bei Bedarf selbst).
# Ein Postfach-Endpunkt, weil Mail.ReadWrite ihn abdeckt; GET /me bräuchte zusätzlich User.Read.
function Get-AccessToken {
    $r = Invoke-Graph -Uri "$api$mbx/mailFolders/inbox?`$select=id" -Raw
    return $r.RequestMessage.Headers.Authorization.Parameter
}

function Get-ChildFolders([string]$FolderId) {
    $uri = "$api$mbx/mailFolders/$FolderId/childFolders?`$top=250&`$select=id,displayName,totalItemCount"
    while ($uri) {
        $r = Invoke-Graph -Uri $uri
        foreach ($v in $r.value) { $v }
        $uri = $r.'@odata.nextLink'
    }
}

function Resolve-Folder([string]$PathOrWellKnown) {
    $wellKnown = 'inbox', 'sentitems', 'deleteditems', 'archive', 'drafts', 'junkemail'
    if ($wellKnown -contains $PathOrWellKnown.ToLower()) {
        return Invoke-Graph -Uri "$api$mbx/mailFolders/$($PathOrWellKnown.ToLower())?`$select=id,displayName,totalItemCount"
    }
    $current = @{ id = 'msgfolderroot' }
    foreach ($name in $PathOrWellKnown.Split('/')) {
        $match = @(Get-ChildFolders $current.id | Where-Object { $_.displayName -eq $name })
        if ($match.Count -eq 0) { throw "Ordner '$name' nicht gefunden (Pfad: $PathOrWellKnown)" }
        if ($match.Count -gt 1) { Write-Warning "Ordnername '$name' mehrfach vorhanden – nehme den ersten." }
        $current = $match[0]
    }
    return $current
}

if (-not $DateField) { $DateField = if ($Source -ieq 'sentitems') { 'sentDateTime' } else { 'receivedDateTime' } }

$src = Resolve-Folder $Source
$dst = Resolve-Folder $TargetPath
if ($src.id -eq $dst.id) { throw "Quelle und Ziel sind derselbe Ordner." }

$cutUtc   = $Before.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$filter   = [uri]::EscapeDataString("$DateField lt $cutUtc")
$firstUri = "$api$mbx/mailFolders/$($src.id)/messages?`$filter=$filter&`$select=id&`$top=$PageSize"

Write-Host ("Quelle : {0}  ({1:N0} Elemente gesamt)" -f $src.displayName, [int]$src.totalItemCount) -ForegroundColor Cyan
Write-Host ("Ziel   : {0}" -f $TargetPath) -ForegroundColor Cyan
Write-Host ("Filter : {0} < {1} (lokal {2:dd.MM.yyyy HH:mm})" -f $DateField, $cutUtc, $Before) -ForegroundColor Cyan

# --- Zählen ---
$total = $null
try {
    $c = Invoke-Graph -Uri "$api$mbx/mailFolders/$($src.id)/messages?`$filter=$filter&`$count=true&`$top=1&`$select=id" -Headers @{ ConsistencyLevel = 'eventual' }
    if ($c.ContainsKey('@odata.count')) { $total = [int]$c.'@odata.count' }
} catch { }
if ($null -eq $total) {
    $total = 0; $u = "$api$mbx/mailFolders/$($src.id)/messages?`$filter=$filter&`$select=id&`$top=1000"
    while ($u) { $r = Invoke-Graph -Uri $u; $total += @($r.value).Count; $u = $r.'@odata.nextLink' }
}
Write-Host ("Zu verschieben: {0:N0}" -f $total) -ForegroundColor Cyan
if ($CountOnly) { return }

# --- Verschieben: bis zu 4 parallele Einzel-Moves ---
$failed    = [System.Collections.Generic.HashSet[string]]::new()
$lastErr   = $null
$done      = 0
$stall     = 0
$uri       = $firstUri
$dstId     = $dst.id
$start     = Get-Date

while ($true) {
    # Vor jeder Seite: MSAL liefert ein Token mit mindestens 5 Minuten Restlaufzeit, eine Seite braucht weniger
    $token = Get-AccessToken

    $r   = Invoke-Graph -Uri $uri
    $ids = @($r.value | ForEach-Object { $_.id } | Where-Object { -not $failed.Contains($_) })

    if ($ids.Count -eq 0) {
        if ($r.'@odata.nextLink') { $uri = $r.'@odata.nextLink'; continue }   # nur noch fehlerhafte Elemente auf dieser Seite
        break
    }
    $uri = $firstUri

    $results = $ids | ForEach-Object -ThrottleLimit $ThrottleLimit -Parallel {
        $id   = $_
        $hdr  = @{ Authorization = "Bearer $($using:token)" }
        $body = (@{ destinationId = $using:dstId } | ConvertTo-Json -Compress)
        $url  = "$($using:api)$($using:mbx)/messages/$id/move"
        for ($t = 1; $t -le 8; $t++) {
            try {
                $null = Invoke-RestMethod -Method POST -Uri $url -Headers $hdr -Body $body -ContentType 'application/json' -TimeoutSec 120
                return [pscustomobject]@{ Id = $id; Ok = $true; Transient = $false; Err = $null }
            }
            catch {
                $code = 0
                try { $code = [int]$_.Exception.Response.StatusCode } catch { }
                if ($code -eq 404) { return [pscustomobject]@{ Id = $id; Ok = $true; Transient = $false; Err = $null } }  # schon weg
                if ($code -eq 429 -or $code -ge 500 -or $code -eq 0) {
                    $wait = 2 * $t
                    try { $ra = $_.Exception.Response.Headers.RetryAfter.Delta; if ($ra) { $wait = [math]::Max($wait, [int][math]::Ceiling($ra.TotalSeconds)) } } catch { }
                    Start-Sleep -Seconds ([math]::Min(60, $wait))
                    continue
                }
                # Token abgelaufen: nicht als Dauerfehler merken, die nächste Seite holt ein neues Token
                if ($code -eq 401) { return [pscustomobject]@{ Id = $id; Ok = $false; Transient = $true; Err = '401 Token abgelaufen' } }
                return [pscustomobject]@{ Id = $id; Ok = $false; Transient = $false; Err = "$code $($_.ErrorDetails.Message)" }
            }
        }
        [pscustomobject]@{ Id = $id; Ok = $false; Transient = $true; Err = 'Wiederholungslimit (Drosselung)' }
    }

    $ok = @($results | Where-Object Ok).Count
    foreach ($x in @($results | Where-Object { -not $_.Ok })) {
        $lastErr = $x.Err
        if (-not $x.Transient) { [void]$failed.Add($x.Id) }
    }
    $done += $ok

    $secs = [math]::Max(1, ((Get-Date) - $start).TotalSeconds)
    $rate = $done / $secs
    $eta  = if ($rate -gt 0) { [timespan]::FromSeconds(($total - $done) / $rate) } else { [timespan]::Zero }
    $pct  = if ($total) { [math]::Min(100, [int](100 * $done / $total)) } else { 100 }
    Write-Progress -Activity "Verschiebe nach $TargetPath" `
        -Status ("{0:N0} / {1:N0}  |  {2:N1} Mails/s  |  Rest ca. {3:hh\:mm}  |  Fehler: {4}" -f $done, $total, $rate, $eta, $failed.Count) `
        -PercentComplete $pct

    if ($ok -eq 0) {
        $stall++
        if ($stall -ge 5) { Write-Warning "Keine Fortschritte mehr – Abbruch. Letzter Fehler: $lastErr"; break }
        Start-Sleep -Seconds 30
    } else { $stall = 0 }
}

Write-Progress -Activity "Verschiebe nach $TargetPath" -Completed
Write-Host ("Fertig: {0:N0} Nachrichten verschoben in {1:hh\:mm\:ss}." -f $done, ((Get-Date) - $start)) -ForegroundColor Green
if ($failed.Count -gt 0) {
    Write-Warning ("{0} Nachrichten konnten nicht verschoben werden. Letzter Fehler: {1}" -f $failed.Count, $lastErr)
}
Write-Host "Zur Kontrolle mit -CountOnly erneut ausführen (sollte 0 ergeben)."