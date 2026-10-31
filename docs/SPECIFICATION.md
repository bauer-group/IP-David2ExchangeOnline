# 📧 Tobit David → Exchange Online: Umsetzungsanleitung david2exchange

> **Status:** Ursprüngliche technische Spezifikation (v3, 30.10.2026). Sie beschreibt die fachliche Basis und die Leitplanken. Für die **aktuelle Implementierung und Bedienung** sind [README](../README.md), [CONFIG](CONFIG.md), [SETUP](SETUP.md) und [RELEASE](RELEASE.md) maßgeblich. Abweichungen zwischen Spezifikation und Umsetzung sind dort dokumentiert.

Migration der Tobit-David-Postfächer (Server SRV-BAUER, david 12.00a) nach Exchange Online. Stand: 30.10.2026 (v3: automatisiertes Tenant-Setup; v2: Dry-Run, Testmodus, Skip, Wiederaufnahme) · Lebensdauer des Tools: einmaliger Einsatz, max. 90 Tage · Zielsprache Code/Kommentare: Deutsch oder Englisch, Logs/Report: Deutsch.


---

## 0. Leitplanken

| Thema | Entscheidung |
|-------|--------------|
| Runtime | .NET 10, **x86**, Windows, eine Konsolen-EXE, ein Prozess |
| Quelle | DvApi32 (COM, 32-Bit) auf einem Rechner mit installiertem David-Client, Anmeldung als angemeldeter Windows-Benutzer |
| Ziel  | Exchange Online per **EWS** (Managed-API-Port für .NET Standard), app-only mit `full_access_as_app`, App-ID in der tenantweiten EWS-Allowlist |
| Einrichtung | `david2exchange setup` richtet den Tenant selbst ein: Entra-App anlegen, Zertifikat erzeugen und hinterlegen, `full_access_as_app` + Admin-Consent, App-ID an die **vorhandene** EWS-Allowlist anhängen, alles verifizieren. Einmaliger interaktiver Admin-Login, keine manuellen PowerShell-Schritte. `setup --remove` baut alles wieder ab. |
| Zustand | SQLite-Manifest (eine Datei), idempotent, Delta = vollständiger Scan + Diff |
| Wiederaufnahme | Jeder Lauf ist jederzeit abbrechbar (Strg+C, Absturz, Stromausfall) und wird durch erneutes `run` exakt dort fortgesetzt, wo er stand – ohne Duplikate, ohne Neuexport bereits exportierter Items |
| Fehlertoleranz | Einzelne fehlerhafte Items blockieren nie den Lauf; sie werden übersprungen, protokolliert und können gezielt ausgeschlossen werden |
| Testbarkeit | `--dry-run` (nichts schreiben) und `--limit N --newest` (nur die N neuesten Items je Postfach) für Probeläufe gegen Testpostfächer |
| Lebensdauer | EWS wird am **1.4.2027** endgültig abgeschaltet. Initial, Deltas und Cutover müssen davor abgeschlossen sein. |
| Nicht bauen | Graph-/IMAP-Adapter, Multi-Targeting net48, Verschiebe-/Lösch-Synchronisation, GUI |
| Aufbewahrung | Alles, was in David liegt, kommt mit (Mail, Fax, SMS, Chat, Sprache, Kalender, Kontakte). Jedes Item trägt Provenienz-Header. Manifest + Abschlussreport sind das Migrationsprotokoll und werden aufbewahrt. |

Das Ziel-Interface `ITargetMailbox` existiert mit genau einer Implementierung (`EwsTargetMailbox`). Kein zweiter Adapter, keine Abstraktion über das hinaus, was der EWS-Adapter braucht.


---

## 1. Voraussetzungen (vor der ersten Codezeile)

### 1.1 Migrationsrechner

- [ ] Windows 10/11 oder Server 2019+, 64-Bit-OS
- [ ] David-Client (Info Center / `DvApi32.dll`, `DVOBJAPI`) installiert und mit SRV-BAUER verbunden; Anmeldung mit einem Windows-Benutzer, der in David Rechte auf **Unverteilt**, das eigene Archiv und alle weiteren zu migrierenden Archive hat
- [ ] .NET 10 SDK (x86-Build möglich), Visual Studio 2026 oder `dotnet` CLI
- [ ] PowerShell 7.4+ (`pwsh`) für `setup`; die Module `Microsoft.Graph.Applications` und `ExchangeOnlineManagement` installiert `setup` bei Bedarf selbst im CurrentUser-Scope
- [ ] Freier Plattenplatz im Staging ≥ 1,5 × Gesamtgröße der Quellarchive
- [ ] Ausgehend HTTPS zu `outlook.office365.com`, `login.microsoftonline.com`, `graph.microsoft.com`

### 1.2 Entra-App-Registrierung (Tenant-Admin)

**Wird von** `**david2exchange setup**` **automatisch erledigt (Abschnitt 1.5).** Die manuellen Schritte hier sind der Fallback, falls das Setup im Tenant nicht erlaubt ist, und die Referenz dafür, was `setup` genau tut.

- [ ] Entra ID → App-Registrierungen → Neu: `david2exchange-migration`, Single Tenant
- [ ] API-Berechtigungen → Hinzufügen → „APIs, die meine Organisation verwendet“ → **Office 365 Exchange Online** → Anwendungsberechtigungen → `**full_access_as_app**` → Admin-Zustimmung erteilen
- [ ] Zertifikate & Geheimnisse → **Zertifikat** hochladen (selbstsigniert reicht, Laufzeit 6 Monate; `.pfx` auf dem Migrationsrechner im `CurrentUser\My`-Store)
- [ ] Application (client) ID und Tenant ID notieren → `migration.yaml`

Optional, empfohlen: Zugriff auf die Migrationspostfächer begrenzen.

```powershell
Connect-ExchangeOnline
# Mail-aktivierte Sicherheitsgruppe "david2exchange-targets" vorher anlegen, Zielpostfächer als Mitglieder
New-ApplicationAccessPolicy -AppId "<APP-ID>" -PolicyScopeGroupId david2exchange-targets@example.com -AccessRight RestrictAccess -Description "david2exchange"
Test-ApplicationAccessPolicy -AppId "<APP-ID>" -Identity max.mustermann@example.com   # muss "Granted" liefern
```

### 1.3 EWS-Allowlist erweitern (Tenant-Admin)

**Wird von** `**david2exchange setup**` **automatisch erledigt (Abschnitt 1.5).** Manuell nur als Fallback.

Die Allowlist ist pro App-ID. `Set-OrganizationConfig -EwsAllowedAppIDs` **ersetzt** die gesamte Liste – Veeam-ID und Microsoft-IDs müssen erhalten bleiben.

```powershell
Connect-ExchangeOnline
$cfg = Get-OrganizationConfig -RetrieveEwsOperationAccessPolicy
$cfg | Format-List EwsEnabled, EwsAllowedAppIDs
$cfg.EwsAllowedAppIDs | Set-Content "C:\temp\ews-allowlist-backup-$(Get-Date -f yyyyMMdd).txt"

$newAppId = "<APP-ID der Migrations-App>"
$existing = @($cfg.EwsAllowedAppIDs -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($existing -notcontains $newAppId) { $existing += $newAppId }
Set-OrganizationConfig -EwsEnabled $true -EwsAllowedAppIDs ($existing -join ',')

Get-OrganizationConfig -RetrieveEwsOperationAccessPolicy | Format-List EwsEnabled, EwsAllowedAppIDs
```

Unbekannte App-IDs in der Liste lassen sich über ewsappid.com zuordnen.

### 1.4 Zielpostfächer

- [ ] Benutzerpostfächer: lizenziert, existieren
- [ ] Archivkonten (weitere David-Konten) und Unverteilt: **Shared Mailboxes** vorher anlegen (`New-Mailbox -Shared`), max. 50 GB ohne Lizenz, kein Litigation Hold, kein Archiv
- [ ] Das Tool legt **keine** Postfächer an

### 1.5 Automatisiertes Tenant-Setup (`david2exchange setup`)

Ziel: Ein Admin startet einen Befehl, meldet sich zweimal interaktiv an (Graph, Exchange Online), und danach ist der Tenant fertig – ohne PowerShell-Handarbeit, ohne Portal-Klicks. Benötigte Rolle: Global Admin oder Application Administrator + Exchange Administrator.

**Technik:** Das Tool bringt zwei PowerShell-Skripte als eingebettete Ressourcen mit (`Setup/tenant-setup.ps1`, `Setup/tenant-remove.ps1`) und startet sie über `pwsh` als Kindprozess; Ausgabe wird live durchgereicht, Ergebnis als JSON auf stdout zurückgelesen. Grund für PowerShell statt Graph-SDK: Die EWS-Allowlist ist nur über `Set-OrganizationConfig` (ExchangeOnlineManagement) erreichbar, nicht über Graph. Dann ist es konsistenter, auch die App-Registrierung per Microsoft.Graph-Modul zu machen.

**Ablauf** `**setup**`**:**

- [ ] `pwsh` ≥ 7.4 prüfen; Module `Microsoft.Graph.Applications`, `Microsoft.Graph.Identity.SignIns`, `ExchangeOnlineManagement` prüfen, bei Fehlen nach Rückfrage `Install-Module -Scope CurrentUser`.
- [ ] `Connect-MgGraph -Scopes "Application.ReadWrite.All","AppRoleAssignment.ReadWrite.All","Directory.Read.All"` (interaktiv, Browser).
- [ ] Zertifikat: selbstsigniert `CN=david2exchange-migration`, RSA 2048, 6 Monate, in `CurrentUser\My`; öffentlicher Teil als `.cer` neben der Config. Existiert bereits ein gültiges mit diesem CN → wiederverwenden.
- [ ] App-Registrierung `david2exchange-migration` anlegen (idempotent: existiert sie nach DisplayName, wird sie aktualisiert, nie dupliziert): `signInAudience = AzureADMyOrg`, `requiredResourceAccess` = Office 365 Exchange Online (`00000002-0000-0ff1-ce00-000000000000`) mit App-Rolle `full_access_as_app` (`dc890d15-9560-4a4c-9b7f-a736ec74ec40`), Key Credential = Zertifikat.
- [ ] Service Principal für die App anlegen; Admin-Consent als `New-MgServicePrincipalAppRoleAssignment` auf den Exchange-Online-Service-Principal mit derselben Rollen-ID.
- [ ] `Connect-ExchangeOnline` (interaktiv). Allowlist lesen (`Get-OrganizationConfig -RetrieveEwsOperationAccessPolicy`), **Backup der kompletten Liste** nach `setup-backup-<datum>.json` (zusammen mit `EwsEnabled`-Zustand), App-ID anhängen, falls nicht enthalten, `Set-OrganizationConfig -EwsEnabled $true -EwsAllowedAppIDs <gesamte Liste>`. Erneut lesen, Vorhandensein prüfen, Anzahl der übrigen Einträge gegen das Backup vergleichen – ist ein Eintrag verschwunden, Abbruch mit Fehler und Restore aus dem Backup.
- [ ] Optional, wenn `m365.accessPolicyGroup` in der Config gesetzt: `New-ApplicationAccessPolicy … RestrictAccess` (idempotent) und `Test-ApplicationAccessPolicy` für jede Ziel-UPN.
- [ ] `tenantId`, `clientId`, `certThumbprint` in `migration.yaml` eintragen (Datei wird aktualisiert, Kommentar mit Zeitstempel).
- [ ] Verifikation: Token per Zertifikat holen (MSAL, `https://outlook.office365.com/.default`), `Folder.Bind(MsgFolderRoot)` auf das erste Zielpostfach. Consent und Allowlist replizieren verzögert – bis zu 10 Minuten mit 30-s-Intervall wiederholen, dann Erfolg oder Klartext-Fehler.

`**setup --check**`**:** nur Schritt 9 plus Anzeige des Allowlist-Zustands. Für die tägliche Vorabprüfung, ob EWS noch erreichbar ist (Scream-Tests).

`**setup --remove**`**:** nach Bestätigung App-ID aus der Allowlist entfernen (alle anderen Einträge bleiben, `EwsEnabled` wird **nicht** verändert), Access Policy löschen, Service Principal und App-Registrierung löschen, Zertifikat aus dem Store entfernen, Werte in `migration.yaml` leeren. Nachher Allowlist erneut lesen und gegen Backup prüfen.

**Was** `**setup**` **nie tut:** Andere App-IDs aus der Allowlist entfernen, `EwsEnabled` auf `$false` setzen, Postfächer anlegen, Lizenzen zuweisen.


---

## 2. Spike (Pflicht, 1–2 h, vor dem eigentlichen Bau)

Drei Tests in einer Wegwerf-Konsolen-App. Jeder hat ein hartes Abnahmekriterium. Scheitert einer, zuerst klären, nicht weiterbauen.

| \#  | Test | Abnahme |
|-----|------|---------|
| S1  | EWS `CreateItem` mit `MimeContent` einer EML + `PidTagMessageFlags=1` + `PR_MESSAGE_DELIVERY_TIME` in ein Testpostfach | Mail erscheint in Outlook/OWA als **empfangene** Mail (kein Entwurf), Datum = gesetztes Datum, Anhänge vorhanden, keine Lesebestätigung angefordert |
| S2  | DvApi: je ein Fax-, SMS-, Sprach- und Chat-Item als Datei exportieren | Für jeden Typ ist die Export-Methode und das Dateiformat bekannt (Methode + Signatur in `SPIKE.md` dokumentieren) |
| S3  | EWS `Contact` mit vCard als `MimeContent` anlegen | Entweder: alle Felder (Name, Firma, 3 Telefonnummern, 2 Mail, Adresse, Notiz) da → vCard-Import verwenden. Oder: Felder fehlen → Feldmapping (Abschnitt 8.5) umsetzen |

Zusätzlich in S2 dokumentieren: Enum-Werte `DvArchiveTypes` und `DvItemTypes` (aus der Typelib im VS-Objektbrowser), Property-Namen für Empfangs-/Sendedatum und Lesestatus, Methode für Zugriff auf das Unverteilt-Archiv (`GetSpecialArchive(...)` oder `GetArchive(<Pfad>)`), Eindeutigkeit von `Item._ID` innerhalb eines Archivs.

Für S1 die App per `setup` anlegen – damit ist das Setup-Skript gleich mitgetestet.


---

## 3. Projektstruktur

```
david2exchange/
  David2Exchange.sln
  src/David2Exchange/            (einziges Projekt)
    David2Exchange.csproj
    Program.cs                   CLI: setup | discover | run | skip | unskip | reset | report
    Config/MigrationConfig.cs    YAML-Modell + Validierung (+ Rückschreiben der m365-Werte durch setup)
    Manifest/Manifest.cs         SQLite-Zugriff (reines Microsoft.Data.Sqlite, kein ORM)
    Setup/TenantSetup.cs         pwsh-Kindprozess, JSON-Rückgabe, Verifikation per EWS
    Setup/tenant-setup.ps1       eingebettete Ressource
    Setup/tenant-remove.ps1      eingebettete Ressource
    Source/DavidSession.cs       COM-Logon, Archiv-Enumeration
    Source/DavidExporter.cs      Item → Staging (EML / synthetische MIME / Kalenderdaten / vCard)
    Source/MimeSynth.cs          Fax/SMS/Chat/Voice → RFC-5322, Header-Strip, Provenienz
    Target/ITargetMailbox.cs
    Target/EwsTargetMailbox.cs   Ordner, CreateItems, Kalender, Kontakte, ReadState
    Target/EwsAuth.cs            MSAL-Zertifikat-Token, Refresh
    Runner/DiscoverRunner.cs
    Runner/MigrationRunner.cs    Export → Import → Readstate → Report
    Runner/ReportWriter.cs
  config/migration.example.yaml
  SPIKE.md                       Ergebnisse aus Abschnitt 2
```

### 3.1 csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <PlatformTarget>x86</PlatformTarget>
    <RuntimeIdentifier>win-x86</RuntimeIdentifier>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Exchange.WebServices.NETStandard" Version="1.1.*" />
    <PackageReference Include="Microsoft.Identity.Client" Version="4.*" />
    <PackageReference Include="MimeKit" Version="4.*" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.*" />
    <PackageReference Include="YamlDotNet" Version="16.*" />
    <PackageReference Include="Serilog" Version="4.*" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.*" />
    <PackageReference Include="Serilog.Sinks.File" Version="6.*" />
    <PackageReference Include="System.CommandLine" Version="2.*" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include="Setup\tenant-setup.ps1" />
    <EmbeddedResource Include="Setup\tenant-remove.ps1" />
  </ItemGroup>
  <ItemGroup>
    <!-- Typelib des David-Clients; GUID aus HKCR\TypeLib ("DvApi32") -->
    <COMReference Include="DvApi32">
      <Guid>00000000-0000-0000-0000-000000000000</Guid>
      <VersionMajor>1</VersionMajor>
      <VersionMinor>0</VersionMinor>
      <Lcid>0</Lcid>
      <WrapperTool>tlbimp</WrapperTool>
      <EmbedInteropTypes>false</EmbedInteropTypes>
    </COMReference>
  </ItemGroup>
</Project>
```

Falls die `COMReference` nicht baut (alte Typelib): Late-Binding über `Type.GetTypeFromProgID("DVOBJAPILib.DvISEAPI")` + `dynamic`. Beides ist unter .NET 10 x86 lauffähig.


---

## 4. Konfiguration `migration.yaml`

```yaml
david:
  server: SRV-BAUER
  archiveRoot: \\SRV-BAUER\david\archive
  timezone: Europe/Berlin            # DvApi liefert lokale Serverzeit → nach UTC wandeln
  logon: AUTH                        # angemeldeter Windows-Benutzer

m365:
  tenantId: ""                       # wird von `setup` gefüllt
  clientId: ""                       # wird von `setup` gefüllt
  certThumbprint: ""                 # wird von `setup` gefüllt (CurrentUser\My)
  appDisplayName: david2exchange-migration
  accessPolicyGroup: ""              # optional: david2exchange-targets@example.com → RestrictAccess
  ewsUrl: https://outlook.office365.com/EWS/Exchange.asmx
  provenanceDomain: david.srv-bauer.local   # für synthetische Absender/Message-IDs

staging:
  root: D:\david2exchange\staging
  manifest: D:\david2exchange\manifest.db
  logs: D:\david2exchange\logs

limits:
  batchMaxItems: 40
  batchMaxBytes: 25000000            # 25 MB pro CreateItems-Request
  singleItemMaxBytes: 120000000      # darüber: skipped/oversize, nur Report
  parallelMailboxes: 2
  retries: 3
  onError: skip                      # skip = fehlerhafte Items überspringen (Default) | stop = Lauf abbrechen

folderMap:                            # global, pro Postfach überschreibbar
  "Eingang":   { special: Inbox }
  "Ausgang":   { special: SentItems }
  "Gesendet":  { special: SentItems }
  "Entwürfe":  { path: "David-Entwürfe" }      # NICHT Drafts
  "Papierkorb":{ path: "David-Papierkorb" }    # NICHT DeletedItems (Aufbewahrung)
  "*":         { path: "" }                     # alles andere 1:1 unter Root

mailboxes:
  - id: kbauer
    source: { kind: user, archivePath: user\00000003, davidUser: KBAUER }
    target: { upn: max.mustermann@example.com }
    include: [mail, fax, sms, chat, voice, calendar, contacts]
    excludeArchives: []

  - id: unverteilt
    source: { kind: common, archivePath: common\unverteilt }
    target: { upn: david-unverteilt@example.com, folderPrefix: "" }
    include: [mail, fax, sms, voice]
    excludeArchives: []

  - id: archiv-mueller
    source: { kind: user, archivePath: user\00000007 }
    target: { upn: david-archiv-mueller@example.com }   # Shared Mailbox
    include: [mail, fax, sms, chat, voice, calendar]           # contacts fehlt → Adressbuch wird übersprungen
    excludeArchives: ["Chat\\Archiv 2015"]
```

Validierung beim Laden (hart, Abbruch bei Fehler): Pflichtfelder, `include` nur aus der bekannten Menge, `archivePath` relativ, keine doppelten `id`, UPN-Format. Mehrere Quellen auf dieselbe UPN sind erlaubt, dann muss `folderPrefix` je Eintrag unterschiedlich und nicht leer sein. Leere `m365.tenantId/clientId/certThumbprint` sind nur für `setup` zulässig; alle anderen Kommandos brechen mit „zuerst `setup` ausführen“ ab.


---

## 5. Manifest (SQLite)

### 5.0 Entscheidung: nicht SQLite, LiteDB

Beide sind embedded und brauchen keinen Server. SQLite ist gesetzt, weil das Manifest nach dem Cutover als **Aufbewahrungsnachweis** liegen bleibt und noch in Jahren mit jedem beliebigen Werkzeug (DB Browser for SQLite, PowerShell, Excel-Import) ohne .NET und ohne das Tool lesbar sein muss. LiteDB wäre für den Entwickler bequemer (POCOs statt SQL), erzeugt aber ein proprietäres Format, das nur mit LiteDB-Bibliothek oder -Studio zu öffnen ist. Für ein 90-Tage-Tool mit Jahren Nachweispflicht ist das das falsche Verhältnis. `Microsoft.Data.Sqlite` mit WAL-Modus, keine ORM-Schicht; die Handvoll SQL-Statements liegen in `Manifest.cs`.


:::info
Wir Nutzen LiteDB da es uns einiges an Aufwand spart und SQLITE gar nicht relevant für eine Aufbewahrung ist.

:::

Alle Zustände – Runs, Ordner, Items, Fehler, Übersprungenes – liegen ausschließlich in dieser einen Datei. Es gibt keinen zweiten Zustandsspeicher (keine JSON-Checkpoints, keine Marker-Dateien im Staging). Die Staging-Dateien sind Nutzdaten, das Manifest ist die Wahrheit.

```sql
PRAGMA journal_mode=WAL;

CREATE TABLE runs (
  run_id          INTEGER PRIMARY KEY,
  started_utc     TEXT NOT NULL,
  finished_utc    TEXT,
  args            TEXT NOT NULL,        -- Kommandozeile
  david_clock_utc TEXT,                 -- Serverzeit beim Start (Skew-Kontrolle)
  tool_version    TEXT NOT NULL
);

CREATE TABLE folders (
  mailbox_id      TEXT NOT NULL,
  src_path        TEXT NOT NULL,        -- Archivpfad relativ, Originalname
  dst_path        TEXT NOT NULL,        -- bereinigter Zielpfad
  dst_id          TEXT,                 -- EWS FolderId.UniqueId
  item_count_src  INTEGER,
  excluded        INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (mailbox_id, src_path)
);

CREATE TABLE items (
  mailbox_id      TEXT NOT NULL,
  src_path        TEXT NOT NULL,
  david_id        TEXT NOT NULL,        -- Item._ID
  item_type       TEXT NOT NULL,        -- mail|fax|sms|chat|voice|calendar|contact
  message_id      TEXT,                 -- RFC Message-ID (echt oder synthetisch), normalisiert
  size_bytes      INTEGER,
  src_received    TEXT,                 -- UTC ISO-8601
  src_sent        TEXT,
  src_read        INTEGER,              -- Lesestatus zum Zeitpunkt des Exports
  first_seen_run  INTEGER NOT NULL,
  last_seen_run   INTEGER NOT NULL,
  exported_utc    TEXT,
  imported_utc    TEXT,
  readstate_utc   TEXT,                 -- letzter Lesestatus-Abgleich
  dst_path        TEXT,
  dst_id          TEXT,                 -- EWS ItemId.UniqueId
  state           TEXT NOT NULL,        -- discovered|exported|importing|imported|failed|skipped
  attempts        INTEGER NOT NULL DEFAULT 0,
  last_error      TEXT,
  skip_reason     TEXT,                 -- gesetzt bei state=skipped: unknown-type|oversize|manual|auto-after-retries
  staging_file    TEXT,
  PRIMARY KEY (mailbox_id, src_path, david_id)
);
CREATE INDEX ix_items_state  ON items(state);
CREATE INDEX ix_items_msgid  ON items(mailbox_id, message_id);
CREATE INDEX ix_items_recv   ON items(mailbox_id, src_received);
```

Regeln:

- [ ] Alle Zeitstempel UTC, ISO-8601 mit `Z`.
- [ ] Jede Statusänderung ist ein eigener `UPDATE`, Commit sofort. Kein Batch-Commit über viele Items.
- [ ] Vor einem EWS-Import wird `state='importing'` geschrieben; nach erfolgreichem Response `imported` + `dst_id`. Ein Absturz dazwischen hinterlässt `importing` → beim nächsten Lauf Reconcile über `message_id` (Abschnitt 10.3).

### 5.1 Zustandsautomat und Wiederaufnahme

```
discovered ──export──▶ exported ──import──▶ importing ──ok──▶ imported
     │                    │                    │
     │ Exportfehler       │                    │ Fehler / Absturz
     ▼                    │                    ▼
   failed ◀───────────────┘            (Reconcile beim nächsten run)
     │ attempts < retries → discovered         │ Treffer → imported
     │ attempts ≥ retries → skipped            │ kein Treffer → discovered
     ▼ (auto-after-retries)
   skipped  ◀── manuell (skip-Kommando), unknown-type, oversize
```

Wiederaufnahme ist kein eigener Modus: `run` arbeitet immer nur die Items ab, die nicht `imported` oder `skipped` sind. Konkret beim Start jedes Laufs:

- [ ] `importing` → Reconcile (10.3).
- [ ] `exported` mit vorhandener `staging_file` → direkt importieren, **nicht** neu exportieren. Fehlt die Datei (Staging gelöscht) → zurück auf `discovered`.
- [ ] `failed` mit `attempts < limits.retries` → `discovered`; mit `attempts ≥ retries` → `skipped`, `skip_reason='auto-after-retries'`.
- [ ] `discovered` → normal.

Ein unterbrochener Lauf hinterlässt keinen inkonsistenten Zustand, weil jedes Item einzeln committet wird und der einzige Zwei-Phasen-Fall (`importing`) über die Message-ID auflösbar ist. Strg+C wird abgefangen (`Console.CancelKeyPress`): der laufende Batch wird zu Ende geschrieben, danach sauberer Abbruch mit `runs.finished_utc` und Hinweis „Fortsetzen mit: david2exchange run …“.


---

## 6. Quelle: DvApi32

### 6.1 Logon und Enumeration

```csharp
var app = new DvApi32.DavidAPIClass();          // bzw. late-bound
var account = app.Logon("", "", "", "", "", config.David.Logon);   // "AUTH"
```

Pro Mapping-Eintrag das Root-Archiv holen: `account.GetArchive(archiveRoot + "\\" + archivePath)` bzw. für Unverteilt die im Spike ermittelte Methode. Danach rekursiv:

```
walk(archive, relPath):
  if relPath matches excludeArchives → folders.excluded=1, return
  folders.upsert(relPath, archive.AllItems.Count)
  foreach item in archive.AllItems: yield (relPath, item)
  foreach sub in archive.Archives: walk(sub, relPath + "\\" + sub.Name)
```

`AllItems` nur mit Metadaten lesen (ID, Type, Dates, Status, Subject, Size). Inhalte erst im Export-Schritt.

### 6.2 Typzuordnung

`item.Type` → `item_type` über eine Tabelle, die aus `SPIKE.md` übernommen wird. Unbekannte Typen → `skipped` mit `skip_reason='unknown-type'`, `last_error='unknown type <n>'`, im Report ausgewiesen (Pflicht: Report muss zeigen, dass nichts stillschweigend wegfällt).

### 6.3 Export je Typ → Staging

Staging-Pfad: `<root>\<mailbox_id>\<src_path>\<david_id>.<ext>` plus `<david_id>.json` (Sidecar: Typ, Daten, Lesestatus, Originalfelder).

| Typ | Export | Datei |
|-----|--------|-------|
| mail | `DVEmlFromMailItem(item, buf)` → Temp-Datei → ins Staging verschieben | `.eml` |
| fax | Methode aus S2 (Bild als TIFF/PDF) + Metadaten (Absendernummer, Seiten, Dauer, Fax-ID) ins Sidecar | `.tif`/`.pdf` + `.json` |
| sms | Text + Absendernummer + Zeit ins Sidecar | `.json` |
| chat | Verlauf als Text (Teilnehmer, Zeit, Nachrichten) ins Sidecar; Originalexport, falls DvApi einen liefert | `.json` (+ Original) |
| voice | Audiodatei (WAV/MP3) + Anrufer, Dauer ins Sidecar | `.wav` + `.json` |
| calendar | Felder: Betreff, Start, Ende, Ganztägig, Ort, Text, Serienfelder → Sidecar | `.json` |
| contact | Felder → vCard 3.0 (UTF-8) + Sidecar | `.vcf` + `.json` |

P/Invoke korrekt:

```csharp
[DllImport("DvApi32.dll", CharSet = CharSet.Ansi)]
static extern int DVEmlFromMailItem([MarshalAs(UnmanagedType.IDispatch)] object mailItem, StringBuilder path); // path: new StringBuilder(260)
```

Rückgabe `int` (nicht `long`). `!= 0` = Erfolg, Pfad der Temp-Datei steht im Puffer.

Datumswerte: DvApi liefert lokale Serverzeit → mit `config.David.timezone` nach UTC. Fehlt ein Empfangsdatum (Postausgang), `src_received = src_sent`.

### 6.4 Serien im Kalender

Liefert DvApi Serien als Master mit Regel: expandieren nach einfachen Regeln (täglich/wöchentlich/monatlich/jährlich, Intervall, Enddatum oder Anzahl), **Horizont: Vergangenheit komplett, Zukunft bis heute + 365 Tage**. Nicht abbildbare Regeln → Master als Einzeltermin + Hinweis im Body „Serientermin, Regel nicht übernommen: <Rohdaten>“. Liefert DvApi bereits Einzelinstanzen, nichts tun.


---

## 7. Normalisierung (`MimeSynth`)

### 7.1 Echte Mails

EML mit MimeKit laden, dann:

- [ ] Header entfernen: `Disposition-Notification-To`, `Return-Receipt-To`, `X-Confirm-Reading-To`, `Read-Receipt-To`. Originalwert nach `X-David-Original-DNT`.
- [ ] Fehlt `Message-ID` → synthetisch (7.3). Fehlt `Date` → aus `src_sent`/`src_received`.
- [ ] Provenienz-Header anhängen (7.4).
- [ ] Zurück in Bytes (`MimeMessage.WriteTo`, `FormatOptions.Default` mit `International=false`).

### 7.2 Synthetische Nachrichten

| Typ | From | Subject | Body (text/plain, zusätzlich text/html optional) | Anhang |
|-----|------|---------|--------------------------------------------------|--------|
| fax | `"Fax +49 9971 123456" <fax.499971123456@<provenanceDomain>>` | `Fax von +49 9971 123456 (3 Seiten)` | Absender, Empfangszeit (lokal + UTC), Seiten, Dauer, Fax-ID, David-Archivpfad | TIFF/PDF, `Content-Disposition: attachment` |
| sms | `"SMS +49 171 …" <sms.49171…@…>` | `SMS von +49 171 …` | Nachrichtentext, Zeit                            | –      |
| chat | `"Chat <Teilnehmer>" <chat.<slug>@…>` | `Chat mit <Teilnehmer> am <Datum>` | Verlauf zeilenweise `[Zeit] Name: Text`          | Original, falls vorhanden |
| voice | `"Anruf +49 …" <voice.49…@…>` | `Sprachnachricht von +49 … (0:42)` | Anrufer, Zeit, Dauer                             | Audio  |

`To:` = UPN des Zielpostfachs. `Date:` = `src_received`. Nummern: E.164 ohne Leerzeichen in der Adresse, lesbar im Anzeigenamen; unbekannte Nummer → `unbekannt`.

### 7.3 Message-ID

Echte Mails: vorhandene ID normalisiert (Trim, `<…>` sicherstellen, lowercase nur für Vergleich im Manifest, nicht im Header). Synthetisch, deterministisch: `<david-{mailbox_id}-{src_path-hash8}-{david_id}@{provenanceDomain}>`. Dieselbe Quelle ergibt immer dieselbe ID → Dedupe über Läufe hinweg.

### 7.4 Provenienz-Header (an jedem Item)

```
X-David-Source: SRV-BAUER
X-David-Archive: user\00000003\Eingang\Fax
X-David-ItemId: 0000ABCD
X-David-ItemType: fax
X-David-Received: 2021-03-04T08:12:00Z
X-David-Migrated: 2026-11-12T19:40:11Z
```


---

## 8. Ziel: EWS (`EwsTargetMailbox`)

### 8.1 Service

```csharp
var svc = new ExchangeService(ExchangeVersion.Exchange2013_SP1)
{
    Url = new Uri(cfg.M365.EwsUrl),
    Credentials = new OAuthCredentials(await auth.GetTokenAsync()),   // Scope https://outlook.office365.com/.default
    ImpersonatedUserId = new ImpersonatedUserId(ConnectingIdType.SmtpAddress, upn),
    Timeout = 300_000
};
svc.HttpHeaders["X-AnchorMailbox"] = upn;
svc.UserAgent = "david2exchange/1.0";
```

Token via MSAL `ConfidentialClientApplicationBuilder.Create(clientId).WithCertificate(cert).WithTenantId(tenantId)`, `AcquireTokenForClient(scopes)`. Vor jedem Batch prüfen, ob Token < 5 min Restlaufzeit → erneuern und `Credentials` neu setzen.

### 8.2 Ordner

- [ ] Root = `WellKnownFolderName.MsgFolderRoot` (bzw. darunter `folderPrefix`, falls gesetzt).
- [ ] `special: Inbox|SentItems` → `WellKnownFolderName`. Sonst Pfadsegmente der Reihe nach: `FindFolders(parent, SearchFilter.IsEqualTo(FolderSchema.DisplayName, name), FolderView(2))`; nicht gefunden → `new Folder(svc){DisplayName=name}.Save(parentId)`.
- [ ] Sanitizing: `/`, `\`, Steuerzeichen → `_`; führende/abschließende Leerzeichen und Punkte entfernen; max. 255 Zeichen; leer → `_`. Kollisionen nach Bereinigung (case-insensitiv) → Suffix ` (2)`, `discover` meldet sie.
- [ ] Leere Quellordner werden angelegt.
- [ ] `dst_id` im Manifest cachen. Zu Beginn jedes `run` einmal `FindFolders(root, Traversal.Deep)` und Cache gegen Realität prüfen; fehlende Ordner neu anlegen, `dst_id` aktualisieren.

### 8.3 Mails und synthetische Nachrichten

```csharp
static readonly ExtendedPropertyDefinition PidTagMessageFlags        = new(0x0E07, MapiPropertyType.Integer);
static readonly ExtendedPropertyDefinition PidTagMessageDeliveryTime = new(0x0E06, MapiPropertyType.SystemTime);
static readonly ExtendedPropertyDefinition PidTagClientSubmitTime    = new(0x0039, MapiPropertyType.SystemTime);
static readonly ExtendedPropertyDefinition PidTagReadReceiptReq      = new(0x0029, MapiPropertyType.Boolean);
static readonly ExtendedPropertyDefinition PidTagDeliveryReportReq   = new(0x0023, MapiPropertyType.Boolean);

var msg = new EmailMessage(svc) { MimeContent = new MimeContent("UTF-8", bytes), IsRead = item.SrcRead };
msg.SetExtendedProperty(PidTagMessageFlags, item.SrcRead ? 1 : 0);   // nie Bit 8 (UNSENT) → kein Entwurf
msg.SetExtendedProperty(PidTagMessageDeliveryTime, item.SrcReceivedUtc);
if (item.SrcSentUtc is { } s) msg.SetExtendedProperty(PidTagClientSubmitTime, s);
msg.SetExtendedProperty(PidTagReadReceiptReq, false);
msg.SetExtendedProperty(PidTagDeliveryReportReq, false);
```

Batch: `svc.CreateItems(items, folderId, MessageDisposition.SaveOnly, null)`; Batchgrenzen aus `limits` (Items **und** Bytes). Items > `singleItemMaxBytes` → `skipped` mit `skip_reason=oversize`, nicht versuchen. Response je Item auswerten (`ServiceResult.Success` → `dst_id`; sonst `failed` + ErrorCode/ErrorMessage).

### 8.4 Kalender

```csharp
var a = new Appointment(svc) {
    Subject, Start, End, IsAllDayEvent, Location,
    Body = new MessageBody(BodyType.Text, text + "\n\n" + provenienzBlock),
    IsReminderSet = false, LegacyFreeBusyStatus = LegacyFreeBusyStatus.Busy
};
await a.Save(calendarFolderId, SendInvitationsMode.SendToNone);
```

Keine `RequiredAttendees`, kein `Organizer`. Zeitzone: `svc.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time")` einmal setzen, Start/Ende als lokale Zeit übergeben.

### 8.5 Kontakte

Nach Ergebnis S3:

- [ ] **vCard-Import**: `new Contact(svc){ MimeContent = new MimeContent("UTF-8", vcfBytes) }.Save(contactsFolderId)`.
- [ ] **Feldmapping**: `GivenName, Surname, CompanyName, JobTitle, Department, EmailAddresses[EmailAddress1..3], PhoneNumbers[BusinessPhone, MobilePhone, HomePhone, BusinessFax], PhysicalAddresses[Business, Home], Body (Notizen), FileAs`. Nicht abbildbare Felder als Text in `Body` anhängen – nichts verwerfen.

### 8.6 Lesestatus-Abgleich (`--readstate-days N`)

Für Items mit `state=imported` und `src_received ≥ now−N Tage`: David-Status erneut lesen; weicht er von `src_read` ab →

```csharp
var em = await EmailMessage.Bind(svc, new ItemId(dstId), new PropertySet(BasePropertySet.IdOnly, EmailMessageSchema.IsRead));
em.IsRead = newState;
await em.Update(ConflictResolutionMode.AutoResolve, suppressReadReceipts: true);
```

`src_read` und `readstate_utc` aktualisieren. Nur Mails (nicht fax/sms/…); diese sind beim Import als gelesen markiert, wenn David sie als gelesen führt, sonst ungelesen.

### 8.7 Throttling und Fehler

- [ ] `ServerBusyException` → `BackOffMilliseconds` abwarten (min. 5 s), Batch wiederholen, zählt nicht als Versuch.
- [ ] `ServiceResponseException` mit `ErrorServerBusy`, `ErrorTimeoutExpired`, HTTP 503 → exponentiell 10 s · 2^n, max. 5 min, zählt als Versuch.
- [ ] Alle anderen Fehler → Item `failed`, `attempts++`, `last_error`. Ab `limits.retries` → `skipped` (`auto-after-retries`), Report.
- [ ] **Ein fehlerhaftes Item stoppt nie den Lauf.** Schlägt ein Batch-Request als Ganzes fehl (nicht einzelne Items in der Response), wird der Batch halbiert wiederholt, bis die Einzelitem-Größe erreicht ist; so wird das eine kaputte Item isoliert und die anderen kommen durch.
- [ ] Ausnahme per Konfiguration `limits.onError: stop` (Default `skip`): dann bricht der Lauf beim ersten `failed` ab – nur für die Entwicklung sinnvoll.
- [ ] Manuelles Überspringen: `david2exchange skip --mailbox <id> --item <david_id> [--reason "…"]` setzt `skipped`/`manual`; `unskip` nimmt es zurück. Übersprungene Items stehen im Report mit Grund, damit die Aufbewahrungslücke dokumentiert ist.
- [ ] HTTP 401/403 bei Token oder EWS (App aus der Allowlist gefallen, Consent fehlt, Scream-Test): Lauf sauber beenden mit Hinweis „`setup --check` ausführen“, nichts als `failed` markieren – das ist ein Umgebungsfehler, kein Item-Fehler.
- [ ] Parallelität: `limits.parallelMailboxes` Postfächer gleichzeitig, pro Postfach **eine** EWS-Verbindung sequenziell. Nicht mehr – EWS drosselt pro Postfach.


---

## 9. Kommandos

```
david2exchange setup    --config migration.yaml [--check | --remove]
david2exchange discover --config migration.yaml
david2exchange run      --config migration.yaml [--mailbox <id>] [--readstate-days 30]
                        [--dry-run] [--limit N --newest|--oldest] [--skip-dedupe-check] [--on-error skip|stop]
david2exchange skip     --config migration.yaml --mailbox <id> --item <david_id> [--reason "…"]
david2exchange unskip   --config migration.yaml --mailbox <id> --item <david_id>
david2exchange reset    --config migration.yaml --mailbox <id> (--item <david_id> | --limit N --newest|--oldest)
david2exchange report   --config migration.yaml [--run <id>] [--out report.html] [--export-manifest manifest.csv]
```

### `setup`

Siehe Abschnitt 1.5. Läuft nicht gegen das Manifest, schreibt nur `migration.yaml` (m365-Block) und `setup-backup-<datum>.json`.

### Dry-Run (`--dry-run`)

Vollständiger Lauf ohne Schreiben ins Ziel und ohne Manifest-Änderung an Items: Scan, Ordner-Abgleich (nur lesend – fehlende Zielordner werden gelistet, nicht angelegt), Export ins Staging **wird** durchgeführt (damit S2-Probleme sichtbar werden), Import wird simuliert (Batches gebildet, Größen geprüft, Dedupe-Suche ausgeführt). Ausgabe: was passieren würde, je Postfach und Ordner, inkl. Oversize und unbekannte Typen. Der Run wird mit `args` im Manifest protokolliert, Items bleiben in ihrem bisherigen Zustand.

### Testmodus (`--limit N --newest|--oldest`)

Begrenzt den Lauf auf N Items **je Postfach**, sortiert nach `src_received` (`--newest` = die N jüngsten, `--oldest` = die N ältesten). Typische Verwendung: `run --mailbox kbauer --limit 10 --newest` gegen ein Testpostfach, Ergebnis in Outlook ansehen. Alles andere verhält sich wie ein echter Lauf – die 10 Items sind danach `imported` und werden bei einem späteren Volllauf nicht erneut importiert. Für einen Wiederholungstest mit denselben 10 Items: im Ziel löschen und `david2exchange reset --mailbox kbauer --limit 10 --newest` (setzt genau diese Items auf `discovered` zurück, löscht `dst_id`). `--limit` ohne Sortierflag ist ein Fehler.

### `discover`

- [ ] Config validieren, Manifest anlegen/öffnen, Run anlegen.
- [ ] DvApi-Logon; je Mapping: Archiv öffnen (Fehler → Klartext „Kein Zugriff auf <path> als <Windows-User>“ und Abbruch am Ende mit Exit-Code 2).
- [ ] EWS: je Ziel-UPN `Folder.Bind(MsgFolderRoot)` (Fehler → „Postfach <upn> nicht erreichbar / nicht in Access Policy“).
- [ ] Vollständiger Metadaten-Scan → `folders`, `items` (state `discovered`), Zählung je Typ und Ordner, Summe Bytes je Mapping.
- [ ] Ausgabe: Tabelle je Mapping (Ordner, Items je Typ, GB, ausgeschlossene Teilbäume mit Item-Zahl), Warnung bei > 40 GB auf ein Shared Mailbox, Ordnerkollisionen, unbekannte Item-Typen. Exit 0 = alles grün, 1 = Warnungen, 2 = Fehler.

### `run`

- [ ] Run anlegen, Scan wie `discover` (schnell, Metadaten) → neue Items `discovered`, bekannte `last_seen_run` aktualisieren, `failed` mit `attempts < retries` zurück auf `discovered`.
- [ ] Ordner-Cache abgleichen (8.2).
- [ ] Reconcile: Items in `importing` → im Zielordner nach `message_id` suchen; Treffer → `imported` + `dst_id`; kein Treffer → `discovered`.
- [ ] Export aller `discovered` → `exported` (Staging).
- [ ] Import aller `exported` in Batches → `imported`/`failed`.
- [ ] Optional Lesestatus (8.6).
- [ ] Report schreiben (Abschnitt 11), Run abschließen. `--dry-run` und `--limit` siehe oben; `--on-error` überschreibt `limits.onError` für diesen Lauf.

Initial- und Delta-Lauf sind derselbe Befehl.

### `report`

Report des letzten (oder angegebenen) Runs erneut erzeugen; `--export-manifest manifest.csv` für die Aufbewahrung.


---

## 10. Delta-Logik und Dedupe

### 10.1 Delta

Kein Zeitstempel-Wasserzeichen. Jeder `run` enumeriert alle Quellarchive vollständig (Metadaten) und vergleicht mit dem Manifest über den Primärschlüssel `(mailbox_id, src_path, david_id)`. Unbekannt = neu. Items, die im Lauf nicht mehr gesehen wurden (`last_seen_run < run_id`), werden nur im Report gelistet („in David gelöscht/verschoben seit Import“) – nichts wird im Ziel gelöscht oder verschoben.

### 10.2 Dedupe vor Import

- [ ] Manifest sagt `imported` → überspringen.
- [ ] Sonst `FindItems(dstFolder, IsEqualTo(InternetMessageId, message_id), ItemView(1))`; Treffer → `imported` + `dst_id` übernehmen, kein Import.
- [ ] Nur dann importieren. Kosten: ein FindItems pro Item. Für den Initiallauf per `--skip-dedupe-check` abschaltbar (leeres Ziel); Deltas immer mit Check.

### 10.3 Reconcile nach Absturz

Siehe `run` Schritt 3. `importing` ohne Treffer bedeutet: Request kam nicht an → sicher neu importieren.


---

## 11. Report (`report.html` + `report.json`)

Pro Mapping:

- [ ] Items je Typ: Quelle gesamt / ausgeschlossen / importiert / failed / skipped (mit Grund)
- [ ] Ordner-Tabelle: `src_path`, `dst_path`, Items Quelle, Items Ziel (per `Folder.Bind(...).TotalCount` am Ende des Laufs)
- [ ] Fehlerliste: `david_id`, `src_path`, Typ, Betreff, `last_error`
- [ ] Oversize-Liste
- [ ] Seit Import in David nicht mehr vorhandene Items
- [ ] Lesestatus-Abgleich: Anzahl geändert
- [ ] Laufzeit, Durchsatz, Throttling-Wartezeit gesamt

Gesamt: Zählerstände aller Mappings, Exit-Code wie `discover`.


---

## 12. Logging

Serilog, Console + Datei `logs\run-<run_id>-<yyyyMMdd-HHmm>.log`. Je Item eine Zeile bei Export, Import, Fehler (Level Information für Status, Warning für Retry, Error für `failed`). Keine Mail-Inhalte im Log – nur IDs, Pfade, Betreff gekürzt auf 60 Zeichen. EWS-Tracing (`svc.TraceEnabled`) nur per `--trace-ews`, schreibt in separate Datei. `setup` loggt nach `logs\setup-<datum>.log`, ohne Token oder Zertifikat-Private-Key.


---

## 13. Betriebsablauf

- [ ] **Tenant-Setup** (Admin am Migrationsrechner): `david2exchange setup`, danach `setup --check`. Shared Mailboxes für Archivkonten und Unverteilt vorher anlegen (1.4).
- [ ] **Spike** (Entwickler): `SPIKE.md`.
- [ ] `**discover**` auf dem Migrationsrechner, Ausgabe mit dem Mapping abgleichen, Ausschlüsse bestätigen lassen.
- [ ] **Probelauf**: `run --mailbox <test> --limit 10 --newest` in ein Testpostfach, Ergebnis in Outlook prüfen; danach `run --dry-run` über alle Mappings.
- [ ] **Initial-**`**run**` (`--skip-dedupe-check`), ggf. über Nacht, Postfach für Postfach (`--mailbox`), beginnend mit dem kleinsten.
- [ ] Report prüfen: Zähler Quelle = Ziel + failed + skipped. Stichprobe je Postfach: eine Mail mit Anhang, ein Fax, ein Termin aus der Vergangenheit, ein Kontakt.
- [ ] **Täglicher Delta-**`**run**` per Aufgabenplanung (Benutzerkonto mit David-Rechten, „Ausführen, wenn Benutzer angemeldet“ – DvApi braucht die Sitzung). Unverteilt ggf. stündlich mit `--mailbox unverteilt`. Vor jedem geplanten Lauf `setup --check`, damit ein EWS-Ausfall als solcher gemeldet wird.
- [ ] **Cutover-Tag**:

  
  1. MX auf Exchange Online umstellen; David-POP3-Abruf und -Verteilung deaktivieren.
  2. 60 Minuten warten (Queue leert sich).
  3. `run --readstate-days 30`.
  4. `report --export-manifest`. Report + CSV + Manifest-DB in die Ablage für Aufbewahrungsunterlagen.
  5. David-Server schreibgeschützt lassen, bis der Report abgenommen ist; danach abschalten.
- [ ] Nach Abschluss: `david2exchange setup --remove` (App-ID aus der Allowlist, App-Registrierung, Zertifikat), Backup-JSON der Allowlist aufbewahren.


---

## 14. Abnahmekriterien

- [ ] `setup` auf leerem Tenant-Zustand: App, Consent, Zertifikat, Allowlist-Eintrag vorhanden; `setup --check` grün innerhalb von 10 Minuten
- [ ] `setup` zweimal hintereinander → keine zweite App, keine doppelte App-ID in der Allowlist, keine Fehler
- [ ] Allowlist vor/nach `setup` und nach `setup --remove`: alle fremden App-IDs (Veeam, Microsoft) unverändert vorhanden, `EwsEnabled` unverändert
- [ ] S1–S3 bestanden und in `SPIKE.md` dokumentiert
- [ ] `discover` meldet fehlende David-Rechte und nicht erreichbare Zielpostfächer in Klartext
- [ ] Ein zweiter `run` ohne Änderungen in David importiert **0** Items
- [ ] Prozess-Kill während des Imports, danach erneuter `run` → keine Duplikate im Ziel (Reconcile)
- [ ] Mail mit `Disposition-Notification-To` importiert, in Outlook geöffnet → keine Lesebestätigung
- [ ] Fax erscheint als Mail mit korrektem Absender, Betreff, Datum, Bild als Anhang
- [ ] Unterordner in 4 Ebenen 1:1 im Ziel, leere Ordner vorhanden
- [ ] Termin von 2019 im Zielkalender mit korrekter Zeit (Sommer-/Winterzeit prüfen: je ein Termin im Januar und Juli)
- [ ] `excludeArchives` und fehlendes `include`-Element lassen den Teilbaum vollständig aus und der Report weist die Anzahl aus
- [ ] Report-Zähler stimmen: Quelle = importiert + failed + skipped + ausgeschlossen
- [ ] Throttling-Test: 5.000 Items am Stück ohne Abbruch (Backoff greift)
- [ ] `run --dry-run` verändert weder Ziel noch Item-Zustände im Manifest (Vorher/Nachher-Dump der Tabelle `items` identisch)
- [ ] `run --limit 10 --newest` importiert genau die 10 jüngsten Items je Postfach; anschließender Volllauf importiert sie nicht erneut
- [ ] Strg+C mitten im Import, danach `run` → Lauf setzt fort, Gesamtzahl im Ziel stimmt, keine Duplikate, kein Neuexport bereits exportierter Items (Staging-Zeitstempel unverändert)
- [ ] Absichtlich kaputtes Item (z. B. 0-Byte-EML im Staging) → Item `failed`/`skipped`, Lauf läuft weiter, Report nennt es
- [ ] `skip`/`unskip` wirken; übersprungene Items erscheinen im Report mit Grund
- [ ] App-ID testweise aus der Allowlist entfernt → `run` bricht mit Hinweis auf `setup --check` ab, kein Item wird `failed`


---

## 15. Zu verifizieren in `DvApi32.chm` / Typelib (Teil des Spikes)

| Frage | Wo eintragen |
|-------|--------------|
| Enum-Werte `DvArchiveTypes` (PersonalIn/Out, Unverteilt) | `SPIKE.md`, `Source/DavidSession.cs` Konstanten |
| Enum-Werte `DvItemTypes` für Mail, Fax, SMS, Chat, Voice, Kalender, Kontakt | `Source/DavidExporter.cs` Typtabelle |
| Property-Namen Empfangs-/Sendedatum, Lesestatus (Bitmaske?) | `Source/DavidExporter.cs` |
| Exportmethode für Fax-Bild, Sprachdatei, Chat | `Source/DavidExporter.cs` |
| Zugriff auf gemeinsames Archiv (Unverteilt) | `Source/DavidSession.cs` |
| Ist `Item._ID` innerhalb eines Archivs eindeutig und stabil? Falls nein: Dateiname des Items als Schlüssel | Manifest-PK bleibt, Quelle des Werts ändert sich |
| Liefert DvApi vCard für Kontakte direkt? | `Source/DavidExporter.cs` |
| Serienfelder bei Kalender-Items | 6\.4         |


---

## 16. Nicht-Ziele (bewusst)

Verschieben/Löschen im Ziel nachziehen · Lesestatus älter als N Tage · Graph/IMAP · GUI · Mehrfachläufe nach dem Cutover · Postfächer anlegen · Lizenzen zuweisen · Kalender-Serienregeln 1:1 · Chat-Formatierung über Klartext hinaus · Verwaltung fremder Allowlist-Einträge.


---

## 17. Aufwandsschätzung (intern, Entwicklung mit Coding-Agent)

| Paket | Stunden | Bemerkung |
|-------|---------|-----------|
| Spike S1–S3 | 2       | Pflicht vor allem anderen; entscheidet über Kontakt-Weg und Fax-Export |
| `setup` (PowerShell-Skripte + Wrapper + Verifikation) | 4       | Idempotenz und Allowlist-Backup/Restore sind der Aufwand, nicht das Anlegen |
| Config, Validierung, Manifest (SQLite, Zustandsautomat) | 3       |           |
| DvApi-Exporter (Enumeration, 7 Item-Typen, Sidecars) | 6       | Größte Unsicherheit – hängt an S2 |
| MimeSynth (Header-Strip, synthetische Mails, Provenienz) | 3       |           |
| EWS-Adapter: Auth, Ordner, Mail-Batches, Throttling | 4       |           |
| EWS-Adapter: Kalender (inkl. Serien-Expansion), Kontakte, Lesestatus | 3       |           |
| CLI, `run`-Pipeline, Delta/Dedupe/Reconcile, Strg+C, Dry-Run, Limit, Skip/Reset | 4       |           |
| Report, Logging | 2       |           |
| Abnahmetests nach Abschnitt 14 (gegen Testpostfächer) | 6       | Nicht kürzen – hier fallen die Duplikat- und Receipt-Fehler auf |
| **Summe Entwicklung** | **37**  | ≈ 5 Arbeitstage |
| Risikopuffer DvApi (undokumentierte Enums, Export-Methoden, `_ID`-Stabilität) | \+8     | nur wenn S2 Überraschungen liefert |

Dazu Betrieb (kein Entwickler nötig, aber Zeit): `discover` + Mapping-Abstimmung 2 h, Initial-Lauf unbeaufsichtigt, Report-Prüfung 2 h, Cutover-Tag 3 h.

**Realistisch: 5 Entwicklertage, bis zu 7 bei DvApi-Überraschungen.** Fertigstellung Ende November 2026 lässt bis zum EWS-Aus (1.4.2027) vier Monate für Initial, Deltas und Cutover – mehr als genug.

Zum Vergleich: t2ex Single Server Pro kostet 499 € netto, kann aber weder Delta-Läufe mit Manifest, noch Fax/SMS/Voice als Mail mit Provenienz, noch automatisiertes Tenant-Setup. Für die Aufbewahrungspflicht wäre das Protokoll der Eigenentwicklung ohnehin der entscheidende Punkt.


---

## 18. README.md (beim Anlegen des Repos übernehmen)

Release-Pipeline und Deployment aus den vorhandenen .NET-Exit-Tool-Projekten der BAUER GROUP übernehmen; hier nur die projektspezifische README.

````markdown
# david2exchange

Einmal-Migrationstool: Tobit David (SRV-BAUER) → Exchange Online. Lebensdauer ≤ 90 Tage, danach `setup --remove` und Repo archivieren.

**Dokumentation:** `docs/README.md` · **Konfiguration:** `docs/CONFIG.md`

## Was es tut

- Liest Mails, Faxe, SMS, Chats, Sprachnachrichten, Kalender und Kontakte per DvApi32 (COM) aus David-Archiven – Benutzerkonten und Unverteilt, inkl. aller Unterordner.
- Schreibt sie per EWS (app-only, Zertifikat) in Exchange-Online-Postfächer. Nicht-Mail-Typen werden zu E-Mails mit korrektem Absender, Betreff, Datum und Anhang; jedes Item trägt `X-David-*`-Provenienz-Header.
- Idempotent und wiederaufnehmbar über ein SQLite-Manifest; Delta = erneutes `run`.
- Löst keine Lesebestätigungen, Einladungen oder sonstige Sendevorgänge aus.

## Voraussetzungen

- Windows x64, David-Client installiert, Windows-Benutzer mit David-Rechten auf alle zu migrierenden Archive
- .NET 10 SDK, PowerShell 7.4+
- Tenant-Admin (Global Admin oder Application Admin + Exchange Admin) für `setup`
- Zielpostfächer existieren (Shared Mailboxes für Archivkonten und Unverteilt vorher anlegen)

## Schnellstart

```powershell
# 1. Tenant einrichten: App-Registrierung, Zertifikat, Consent, EWS-Allowlist (fremde Einträge bleiben)
david2exchange setup --config migration.yaml
david2exchange setup --config migration.yaml --check

# 2. Quelle und Ziel prüfen, Mapping gegenlesen
david2exchange discover --config migration.yaml

# 3. Probelauf: 10 neueste Items eines Postfachs, dann Trockenlauf über alles
david2exchange run --config migration.yaml --mailbox kbauer --limit 10 --newest
david2exchange run --config migration.yaml --dry-run

# 4. Initial-Lauf, danach täglich als Delta (gleicher Befehl)
david2exchange run --config migration.yaml --skip-dedupe-check
david2exchange run --config migration.yaml

# 5. Cutover: MX umstellen, David-Abruf stoppen, 60 min warten, dann
david2exchange run --config migration.yaml --readstate-days 30
david2exchange report --config migration.yaml --export-manifest manifest.csv

# 6. Abbau
david2exchange setup --config migration.yaml --remove
```

## Kommandos

| Befehl | Zweck |
|---|---|
| `setup [--check\|--remove]` | Tenant einrichten / prüfen / zurückbauen |
| `discover` | Mapping validieren, Archive zählen, Manifest anlegen |
| `run [--mailbox] [--dry-run] [--limit N --newest\|--oldest] [--readstate-days N] [--on-error skip\|stop]` | Export + Import; Initial und Delta identisch |
| `skip` / `unskip` | Einzelnes Item mit Begründung ausklammern / zurücknehmen |
| `reset` | Items auf `discovered` zurücksetzen (für Wiederholungstests) |
| `report [--run] [--export-manifest]` | HTML/JSON-Report, Manifest-Export für die Aufbewahrung |

## Konfiguration

`config/migration.example.yaml` kopieren → `migration.yaml`. Pro Postfach: David-Archivpfad, Ziel-UPN, `include` (Item-Typen) und `excludeArchives` (ganze Teilbäume). Die `m365`-Werte füllt `setup`.

## Build

```powershell
dotnet publish src/David2Exchange -c Release -r win-x86 --self-contained false
```

x86 ist Pflicht (DvApi32 ist 32-Bit-COM).

## Aufbewahrung

Nach dem Cutover `manifest.db`, `manifest.csv`, `report.html` und `setup-backup-*.json` in die Ablage für Aufbewahrungsunterlagen. Das Manifest ist das Migrationsprotokoll – pro Item Quelle, Ziel, Zeitpunkt, Status.

## Nicht-Ziele

Graph/IMAP-Adapter, GUI, Verschiebe-/Lösch-Synchronisation, Kalender-Serienregeln 1:1, Lesestatus älter als N Tage, Postfächer anlegen. EWS endet am 1.4.2027 – bis dahin muss alles durch sein.
````