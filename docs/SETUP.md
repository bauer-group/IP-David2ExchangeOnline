# Tenant-Setup: automatisch und manuell

Ziel: Eine Entra-App mit Zertifikat, `full_access_as_app`-Consent und einem Eintrag in der **EWS-Allowlist** des Tenants. Danach kann `david2exchange` per EWS app-only auf die Zielpostfächer zugreifen.

> **Wichtig:** EWS wird in Exchange Online seit dem 01.10.2026 global deaktiviert; Ausnahmen gelten nur für App-IDs in `EwsAllowedAppIDs` und enden am 01.04.2027. Die Freigabe kann bis zu 24 Stunden brauchen. Ein Token allein beweist nicht, dass EWS für die App freigegeben ist. Das Tool hat dafür keinen eigenen Test; der erste echte Zugriff ist `run --limit 1 --output ews` und überträgt bereits ein Item.

## 1. Voraussetzungen

| Punkt | Anforderung |
|---|---|
| Rolle | Global Admin **oder** Application Administrator + Exchange Administrator |
| PowerShell | `pwsh` 7.4+ |
| Module | `Microsoft.Graph.Applications`, `Microsoft.Graph.Identity.SignIns`, `ExchangeOnlineManagement` |
| Netzwerk | HTTPS zu `login.microsoftonline.com`, `graph.microsoft.com`, `outlook.office365.com` |
| Zertifikat | privat in `CurrentUser\My`; `setup` erzeugt es selbst |
| Ziel | Zielpostfächer existieren (User/Shared) oder werden per `mailboxes ensure` angelegt |

## 2. Automatisches Setup

```powershell
cd C:\david2exchange
.\david2exchange.exe setup --config migration.yaml
.\david2exchange.exe setup --config migration.yaml --check
```

> **Vor dem Start lesen:** `setup` fragt nicht nach. Es installiert fehlende Module und setzt `EwsEnabled=true` für den **gesamten Tenant**. Damit dürfen alle Apps der Allowlist EWS nutzen, nicht nur diese. Ist das nicht gewünscht, das manuelle Setup in Abschnitt 3 verwenden.

### Was `setup` tut

1. Prüft `pwsh` und die benötigten Module; installiert fehlende Module ohne Rückfrage im `CurrentUser`-Scope.
2. **Device-Code-Login** bei Microsoft Graph (`Application.ReadWrite.All`, `AppRoleAssignment.ReadWrite.All`, `Directory.Read.All`). Die EXE startet `pwsh` ohne Output-Umleitung; URL und Code werden auf der Konsole angezeigt.
3. Erzeugt/verwendet ein selbstsigniertes Zertifikat `CN=david2exchange-migration` (RSA 2048, 12 Monate) in `CurrentUser\My`. Wenn `New-SelfSignedCertificate` nicht antwortet, bricht die Erzeugung nach 90 s ab und fällt automatisch auf `certreq` zurück.
4. Legt die App `david2exchange-migration` an oder aktualisiert sie (Single Tenant, `full_access_as_app` für Office 365 Exchange Online, Zertifikat als Key Credential).
5. Legt den Service Principal an und erteilt Admin-Consent.
6. Exchange-Online-Login per **Device Code**; liest `EwsEnabled`/`EwsAllowedAppIDs`, sichert die **komplette** Liste nach `setup-backup-<datum>.json`, hängt die neue App-ID an (falls nicht vorhanden) und schreibt **ohne Rückfrage** `EwsEnabled=true` samt Liste.
7. Liest die Liste erneut und gibt sie zurück. Sie wird nicht automatisch verglichen: **Bitte selbst prüfen**, dass fremde App-IDs (Veeam, Microsoft) noch vorhanden sind; die alte Liste steht im Backup.
8. Optional: `New-ApplicationAccessPolicy` auf eine Sicherheitsgruppe.
9. Schreibt `tenantId`, `clientId`, `certThumbprint` in `migration.yaml` (Backup `.bak`).

Der Zugriff selbst wird nicht getestet. Das macht der erste Lauf, siehe den Hinweis oben.

### `--check`

Prüft ohne Änderungen und zeigt `ewsEnabled` und `appIdInAllowlist` an:

- App vorhanden,
- `EwsEnabled`,
- App-ID in der Allowlist (auch als kommagetrennter Text).

Nicht geprüft werden Zertifikat, Access Policy, die Werte in `migration.yaml` und der EWS-Zugriff selbst. Der Befehl braucht zwei interaktive Device-Code-Anmeldungen und lässt sich deshalb nicht einplanen.

Exit 0 = alle drei Punkte erfüllt, 1 = etwas fehlt, 3 = Aufruffehler.

### `--remove`

Entfernt **ohne Rückfrage**:

- App-ID aus der Allowlist (alle anderen Einträge bleiben),
- Application Access Policy,
- Service Principal und App-Registrierung,
- `m365`-Werte aus der Config.

Das Zertifikat aus `CurrentUser\My` geht nur mit, wenn `--cert-thumbprint <thumbprint>` angegeben ist; der Wert aus der Config wird dafür nicht gelesen.

`EwsEnabled` wird **nicht** verändert.

## 3. Manuelles Setup (Fallback)

Falls `setup` im Tenant nicht erlaubt ist oder die Automatisierung nicht genutzt werden soll.

### 3.1 App-Registrierung

1. Entra ID → App-Registrierungen → **Neue Registrierung**.
2. Name: `david2exchange-migration`.
3. Unterstützte Kontotypen: **Nur Konten in diesem Organisationsverzeichnis** (Single Tenant).
4. Redirect-URI: keine.
5. Anwendungs-ID (Client-ID) und Verzeichnis-ID (Tenant-ID) notieren.

### 3.2 API-Berechtigung und Consent

1. API-Berechtigungen → Berechtigung hinzufügen.
2. „APIs, die meine Organisation verwendet“ → **Office 365 Exchange Online**.
3. **Anwendungsberechtigungen** → `full_access_as_app`.
4. Admin-Zustimmung erteilen.
5. Prüfen: Status „Erteilt für …“.

### 3.3 Zertifikat

```powershell
$cert = New-SelfSignedCertificate `
  -Subject "CN=david2exchange-migration" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -KeySpec Signature `
  -KeyExportPolicy NonExportable `
  -KeyAlgorithm RSA `
  -KeyLength 2048 `
  -NotAfter (Get-Date).AddMonths(12)

$cert.Thumbprint
Export-Certificate -Cert $cert -FilePath ".\david2exchange-migration.cer"
```

1. In der App: Zertifikate & Geheimnisse → Zertifikat hochladen → `.cer`.
2. Thumbprint in `migration.yaml` unter `m365.certThumbprint` eintragen.
3. Der laufende Benutzer muss den privaten Schlüssel lesen dürfen.

### 3.4 EWS-Allowlist

Die Liste ist pro App-ID. `Set-OrganizationConfig -EwsAllowedAppIDs` **ersetzt** die gesamte Liste.

```powershell
Connect-ExchangeOnline
$cfg = Get-OrganizationConfig -RetrieveEwsOperationAccessPolicy

# Backup der kompletten Liste
$cfg | ConvertTo-Json -Depth 5 |
  Set-Content ".\setup-backup-$(Get-Date -f yyyyMMdd-HHmmss).json"

$newAppId = "<APP-ID>"
$existing = @($cfg.EwsAllowedAppIDs -split "," |
  ForEach-Object { $_.Trim() } | Where-Object { $_ })

if ($existing -notcontains $newAppId) { $existing += $newAppId }

Set-OrganizationConfig -EwsEnabled $true -EwsAllowedAppIDs ($existing -join ",")

# Verifizieren
Get-OrganizationConfig -RetrieveEwsOperationAccessPolicy |
  Format-List EwsEnabled, EwsAllowedAppIDs
```

Regeln:

- **Nie** die Liste auf nur eine ID setzen.
- Fremde IDs (Veeam, Microsoft) müssen erhalten bleiben.
- Bei Abweichung: Backup zurückschreiben.
- Änderungen können bis zu 24 Stunden brauchen. `setup --check` zeigt die Konfiguration, nicht die wirksame Freigabe.

### 3.5 Application Access Policy (empfohlen)

```powershell
# Mail-aktivierte Sicherheitsgruppe "david2exchange-targets" vorher anlegen
New-ApplicationAccessPolicy `
  -AppId "<APP-ID>" `
  -PolicyScopeGroupId david2exchange-targets@example.com `
  -AccessRight RestrictAccess `
  -Description "david2exchange"

Test-ApplicationAccessPolicy `
  -AppId "<APP-ID>" `
  -Identity test@example.com
```

`Granted` = Zugriff erlaubt. Die Policy kann einige Minuten brauchen.

### 3.6 Config-Werte

```yaml
m365:
  tenantId: "<tenant-id>"
  clientId: "<app-id>"
  certThumbprint: "<thumbprint>"
  ewsUrl: https://outlook.office365.com/EWS/Exchange.asmx
  migrationAdminUpn: admin@example.com
```

### 3.7 Verifikation

```powershell
.\david2exchange.exe setup --config migration.yaml --check
```

Erwartet: `ewsEnabled: True`, `appIdInAllowlist: True`, Exit 0. Der Zugriff selbst zeigt sich erst beim ersten Lauf. Bei `403`/`ErrorAccessDenied`:

- App-ID in der Allowlist?
- `EwsEnabled=true`?
- Consent erteilt?
- Access Policy?
- Bis zu 24 h gewartet?

## 4. Fehlende Postfächer anlegen

```powershell
.\david2exchange.exe mailboxes ensure `
  --config migration.yaml `
  --wave wave-1 `
  --admin-upn admin@example.com
```

Das Skript:

- verbindet interaktiv mit Exchange Online,
- legt fehlende **Shared Mailboxes** an und setzt den Anmeldenamen auf die Mailadresse (`Set-Mailbox -MicrosoftOnlineServicesID`). Das gilt auch für vorhandene Shared Mailboxes, deren Hauptadresse der `target.upn` entspricht; ein Alias wird nur gemeldet,
- liest den Anmeldenamen am Ende zurück und meldet `UPN ... bestätigt`. `signInOpen` in der `DONE`-Zeile nennt, was offen blieb; dann den Befehl später erneut ausführen,
- aktiviert bei Shared Mailboxes „Gesendete Elemente kopieren“ für „Als“ und „Im Auftrag“, auch bei vorhandenen. `sentCopyOpen` in der `DONE`-Zeile nennt, was fehlschlug,
- setzt `target.aliases`,
- vergibt `FullAccess` ohne Auto-Mapping an den Admin,
- unterstützt `--dry-run`.

Beispiel manuell:

```powershell
Connect-ExchangeOnline -UserPrincipalName admin@example.com

New-Mailbox -Shared `
  -Name "BAUER GROUP [SG]" `
  -DisplayName "BAUER GROUP [SG]" `
  -Alias "info-sg" `
  -PrimarySmtpAddress info@sg.example.com

Set-Mailbox -Identity info@sg.example.com -MicrosoftOnlineServicesID info@sg.example.com
Set-Mailbox -Identity info@sg.example.com -MessageCopyForSentAsEnabled $true -MessageCopyForSendOnBehalfEnabled $true

Add-MailboxPermission `
  -Identity info@sg.example.com `
  -User admin@example.com `
  -AccessRights FullAccess `
  -InheritanceType All `
  -AutoMapping:$false
```

User-Mailboxes werden nicht automatisch erzeugt: Sie benötigen eine Lizenz und einen aktiven Benutzer. Für ausgeschiedene Mitarbeiter ist die Shared Mailbox der übliche Weg.

## 5. Sicherheit und Aufbewahrung

- Zertifikat nur in `CurrentUser\My`; privater Schlüssel wird nicht exportiert.
- Config enthält keine Secrets; Thumbprint/IDs sind keine Passwörter, aber intern behandeln.
- Setup-Backup der Allowlist aufbewahren.
- `setup --remove --cert-thumbprint <thumbprint>` nach Abschluss; `EwsEnabled` bleibt unverändert und muss bei Bedarf von Hand zurückgesetzt werden.
- Logs enthalten keine Mail-Inhalte.
