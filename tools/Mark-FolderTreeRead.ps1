# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
  Markiert alle ungelesenen Nachrichten in einem Ordner INKL. aller Unterordner als gelesen
  (Microsoft Graph, delegierte Anmeldung). Arbeitet im Primärpostfach – VOR dem Verschieben ins Archiv ausführen.
  Dokumentation: docs/TOOLS.md

.BEISPIELE
  # 1. Nur zählen (ändert nichts)
  .\Mark-FolderTreeRead.ps1 -FolderPath "David-Import" -CountOnly

  # 2. Ausführen
  .\Mark-FolderTreeRead.ps1 -FolderPath "David-Import"

  # Ordner unterhalb des Posteingangs: Pfad mit "/" angeben
  .\Mark-FolderTreeRead.ps1 -FolderPath "Posteingang/David-Import"

.PARAMETER Mailbox
  Postfach (UPN/SMTP). Leer = Postfach des angemeldeten Benutzers. Ein fremdes Postfach braucht
  Vollzugriff darauf; das Skript fordert dann zusätzlich Mail.ReadWrite.Shared an.

.HINWEISE
  - Benötigt PowerShell 7 (empfohlen) oder 5.1; installiert Microsoft.Graph.Authentication bei Bedarf.
  - Abbrechen und erneut starten ist jederzeit möglich – es werden immer nur noch ungelesene Mails bearbeitet.
#>
[CmdletBinding()]
param(
    [string]$Mailbox,
    [Parameter(Mandatory)][string]$FolderPath,
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
Write-Host ("Konto: {0}  |  Postfach: {1}" -f $ctx.Account, $(if ($Mailbox) { $Mailbox } else { $ctx.Account })) -ForegroundColor Cyan

$api = "https://graph.microsoft.com/v1.0"
$mbx = if ($Mailbox) { "/users/$Mailbox" } else { '/me' }

function Invoke-Graph {
    param([string]$Method = 'GET', [string]$Uri, $Body)
    for ($try = 1; $try -le 10; $try++) {
        try {
            if ($null -ne $Body) {
                return Invoke-MgGraphRequest -Method $Method -Uri $Uri -Body ($Body | ConvertTo-Json -Depth 10) -ContentType 'application/json'
            }
            return Invoke-MgGraphRequest -Method $Method -Uri $Uri
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

function Get-ChildFolders([string]$FolderId) {
    $uri = "$api$mbx/mailFolders/$FolderId/childFolders?`$top=250&`$select=id,displayName,childFolderCount,unreadItemCount,totalItemCount"
    while ($uri) {
        $r = Invoke-Graph -Uri $uri
        foreach ($v in $r.value) { $v }
        $uri = $r.'@odata.nextLink'
    }
}

# --- Startordner über den Pfad auflösen ---
$current = @{ id = 'msgfolderroot'; childFolderCount = 1 }
foreach ($name in $FolderPath.Split('/')) {
    $match = @(Get-ChildFolders $current.id | Where-Object { $_.displayName -eq $name })
    if ($match.Count -eq 0) { throw "Ordner '$name' nicht gefunden (Pfad: $FolderPath)" }
    if ($match.Count -gt 1) { Write-Warning "Ordnername '$name' mehrfach vorhanden – nehme den ersten." }
    $current = $match[0]
}
Write-Host "Startordner: $FolderPath" -ForegroundColor Cyan

# --- Gesamten Ordnerbaum einsammeln ---
$all   = [System.Collections.Generic.List[object]]::new()
$queue = [System.Collections.Generic.Queue[object]]::new()
$queue.Enqueue($current)
while ($queue.Count -gt 0) {
    $f = $queue.Dequeue()
    $all.Add($f)
    if ([int]$f.childFolderCount -gt 0) {
        foreach ($c in Get-ChildFolders $f.id) { $queue.Enqueue($c) }
    }
}

$unreadTotal = 0; $itemsTotal = 0
foreach ($f in $all) { $unreadTotal += [int]$f.unreadItemCount; $itemsTotal += [int]$f.totalItemCount }
Write-Host ("Ordner: {0}  |  Elemente: {1:N0}  |  davon ungelesen: {2:N0}" -f $all.Count, $itemsTotal, $unreadTotal) -ForegroundColor Cyan

if ($CountOnly) { return }

# --- Als gelesen markieren (JSON-Batches à 20) ---
$done = 0
$todo = @($all | Where-Object { [int]$_.unreadItemCount -gt 0 })
$fi = 0
foreach ($f in $todo) {
    $fi++
    $stall = 0
    while ($true) {
        $r   = Invoke-Graph -Uri "$api$mbx/mailFolders/$($f.id)/messages?`$filter=isRead%20eq%20false&`$select=id&`$top=200"
        $ids = @($r.value | ForEach-Object { $_.id })
        if ($ids.Count -eq 0) { break }

        $ok = 0
        for ($k = 0; $k -lt $ids.Count; $k += 20) {
            $chunk = @($ids[$k..([math]::Min($k + 19, $ids.Count - 1))])
            $reqs = for ($j = 0; $j -lt $chunk.Count; $j++) {
                @{
                    id      = "$j"
                    method  = 'PATCH'
                    url     = "$mbx/messages/$($chunk[$j])"
                    headers = @{ 'Content-Type' = 'application/json' }
                    body    = @{ isRead = $true }
                }
            }
            $resp = Invoke-Graph -Method POST -Uri "$api/`$batch" -Body @{ requests = @($reqs) }
            $ok  += @($resp.responses | Where-Object { [int]$_.status -eq 200 }).Count
            if (@($resp.responses | Where-Object { [int]$_.status -eq 429 }).Count -gt 0) { Start-Sleep -Seconds 15 }
        }
        $done += $ok

        $pct = if ($unreadTotal) { [math]::Min(100, [int](100 * $done / $unreadTotal)) } else { 100 }
        Write-Progress -Activity "Als gelesen markieren" -Status ("{0:N0} / {1:N0}  –  Ordner {2}/{3}: {4}" -f $done, $unreadTotal, $fi, $todo.Count, $f.displayName) -PercentComplete $pct

        if ($ok -eq 0) {
            $stall++
            if ($stall -ge 5) { Write-Warning "Ordner '$($f.displayName)': keine Fortschritte – übersprungen."; break }
            Start-Sleep -Seconds 30
        } else { $stall = 0 }
    }
}
Write-Progress -Activity "Als gelesen markieren" -Completed
Write-Host ("Fertig: {0:N0} Nachrichten als gelesen markiert. Zur Kontrolle mit -CountOnly erneut ausführen." -f $done) -ForegroundColor Green