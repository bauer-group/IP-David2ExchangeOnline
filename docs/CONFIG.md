# Konfiguration und Postfach-Mapping

Die Datei `migration.yaml` ist die einzige Konfiguration. Vorlage: `config/migration.example.yaml`.

## 1. Ablauf

### 1a. Config automatisch erzeugen

```powershell
# Zielpostfächer einmalig als JSON sichern (Exchange-Admin, interaktiv)
david2exchange targets --json > targets.json

# Quellen + Ziele automatisch 1:1 mappen und migration.yaml schreiben
david2exchange config create `
  --config migration.yaml `
  --targets-file targets.json `
  --domain example.com
```

`config create` listet alle David-Benutzerarchive und Unverteilt-/Common-Archive, liest die David-E-Mail-Adresse aus `User.Addresses` und matcht **ausschließlich über die E-Mail-Adresse** (primär oder Alias) gegen User- und Shared-Mailboxen. Anzeigenamen werden nicht als Match-Schlüssel verwendet.

Nicht sicher zugeordnete Quellen werden mit `enabled: false` und leerem `target.upn` aufgenommen; sie werden nicht migriert, bis sie per `map` ergänzt wurden. `--wave <name>` setzt den Migrationsblock für alle erzeugten Einträge.

Mapping-Report: `migration.mapping.json` (Quelle, Anzeigename, Ziel, Methode).

Offline-Variante (ohne laufenden David-Client), z. B. für Vorbereitung/Review:

```powershell
david2exchange sources --config migration.yaml --json > sources.json
david2exchange config create --config migration.yaml `
  --sources-file sources.json --targets-file targets.json --domain example.com
```

### 1b. Manuell ergänzen

```powershell
david2exchange map --config migration.yaml --list
david2exchange map --config migration.yaml `
  --source "user\1001C000" `
  --target max.mustermann@example.com `
  --id max-mustermann `
  --kind user
```

### 1c. Danach prüfen und migrieren

```powershell
david2exchange sources --config migration.yaml
david2exchange targets
david2exchange discover --config migration.yaml --limit 10
david2exchange run --config migration.yaml --limit 10 --dry-run
david2exchange run --config migration.yaml --limit 10 --output local
david2exchange report --config migration.yaml --out data\report.html
```

## 2. Schema

```yaml
david:
  server: david.example.local          # nur Anzeige/Report
  archiveRoot: \\david.example.local\david\archive
  progId: DVOBJAPILib.DvISEAPI         # je nach Installation
  timezone: Europe/Berlin
  logon: AUTH                          # angemeldeter Windows-Benutzer

m365:
  tenantId: ""                         # setup füllt
  clientId: ""                         # setup füllt
  certThumbprint: ""                   # setup füllt
  ewsUrl: https://outlook.office365.com/EWS/Exchange.asmx
  provenanceDomain: david.example.local
  ewsFolder: David-Migration
  migrationAdminUpn: admin@example.com  # Vollzugriff auf neu angelegte Postfächer

staging:
  root: .\data
  manifest: .\data\manifest.db
  logs: .\logs

limits:
  limit: 10                            # Testlauf; 0 = alle
  retries: 3

mailboxes:
  - id: kbauer                         # eindeutig, technischer Schlüssel
    enabled: true                      # false = wird nicht migriert (z. B. ungemappt)
    wave: wave-1                       # Migrationsblock/Gruppe
    source:
      kind: user                       # user | common
      archivePath: "user\\1000C000"    # aus `sources`
      email: karl.bauer@example.com     # sicherer Match-Schlüssel
    target:
      upn: karl.bauer@example.com      # User- oder Shared-Mailbox
      type: user                       # user | shared
      displayName: Karl Bauer
      alias: karl.bauer
      aliases: []                      # zusätzliche SMTP-Adressen
      createIfMissing: false           # Shared Mailbox bei Bedarf anlegen
    include: [mail]                    # mail, fax, sms, chat, voice, calendar, contacts
    excludeArchives: []                # Teilbäume, z. B. ["ADDRESS"]

  - id: unverteilt
    source:
      kind: common
      archivePath: "common\\unverteilt"
    target:
      upn: david-unverteilt@example.com
    include: [mail]
    excludeArchives: ["ADDRESS", "#001#Adressen"]
```

### Besonderheiten

- **`user\@me`** löst der Adapter über `Account.User.ID` auf (z. B. `user\1000C000`). Für die Migration besser den echten Pfad aus `sources` verwenden.
- **Benutzerarchiv:** `user\<Hex-ID>`; die ID ist nicht die laufende Nummer. `sources` zeigt die korrekten Pfade.
- **Unverteilt:** `common\unverteilt` enthält Adress-Teilarchive. `excludeArchives` oder `include: [mail]` verhindert, dass Typ 1 (Adresse) mitkommt.
- **Shared Mailbox:** einfach die UPN der Shared Mailbox als `target.upn` eintragen. Das Tool legt keine Postfächer an.
- **`enabled`:** `false` schließt den Eintrag aus; `config create` setzt es für nicht automatisch zugeordnete Quellen auf `false`. `map` setzt es beim Mapping wieder auf `true`.
- **`wave`:** Gruppiert Postfächer in Migrationsblöcke. `discover/run/verify --wave <name>` bearbeitet nur diesen Block; pro Block kann auch eine eigene Config verwendet werden.
- **`source.email`:** Pflicht für aktive Benutzerpostfächer; nur diese Adresse wird für das Mapping verwendet.
- **`target.aliases`:** zusätzliche SMTP-Adressen, die beim Anlegen/Ergänzen gesetzt werden.
- **`target.createIfMissing`:** nur für Shared Mailboxes automatisch; User-Mailboxes werden nicht automatisch lizenziert.
- **Mehrere Quellen auf ein Ziel:** erlaubt; `id` muss eindeutig sein.
- **Typen:** 1 = Adresse/Kontakt, 2 = Mail, 7 = Kalender (auf SRV-BAUER beobachtet). Der Lokal-Export schreibt derzeit Mail (Typ 2).

## 3. Quellen listen

```powershell
david2exchange sources --config migration.yaml
david2exchange sources --config migration.yaml --json
```

Ausgabe enthält `Art` (`USER`/`COMMON`), `Pfad` und `Anzeigename`. Es werden nur Benutzer- und gemeinsame Archive der ersten Ebene gelistet, keine Unterordner.

## 4. Zielpostfächer listen

```powershell
david2exchange targets
david2exchange targets --json
```

Das Kommando startet `pwsh` mit `ExchangeOnlineManagement`, meldet sich interaktiv an und listet `UserMailbox` und `SharedMailbox` mit `DisplayName`, `PrimarySmtpAddress` und `RecipientTypeDetails`.

Voraussetzung: Exchange-Admin-Rolle und `pwsh` 7.4+.

## 5. Mapping schreiben

```powershell
david2exchange map --config migration.yaml `
  --source "user\1000C000" `
  --target karl.bauer@example.com `
  --id kbauer `
  --kind user
```

Das Kommando:

- legt den Eintrag an oder aktualisiert ihn über `id`,
- schreibt die YAML mit Backup (`migration.yaml.bak`),
- validiert die UPN grob (`@`),
- setzt `include: [mail]`, falls noch nicht gesetzt.

`--list` zeigt die aktuellen Mappings.

## 6. Postfächer anlegen und Vollzugriff

```powershell
# Shared Mailboxes anlegen, Alias setzen, Vollzugriff für den Migrations-Admin
david2exchange mailboxes ensure --config migration.yaml `
  --wave wave-1 --admin-upn admin@example.com

# Nur prüfen, was passieren würde
david2exchange mailboxes ensure --config migration.yaml `
  --wave wave-1 --admin-upn admin@example.com --dry-run
```

Das Skript verbindet sich interaktiv mit Exchange Online, legt fehlende **Shared Mailboxes** an (`New-Mailbox -Shared`), ergänzt `aliases` per `Set-Mailbox` und vergibt `FullAccess` ohne Auto-Mapping an den Admin. User-Mailboxes werden nicht automatisch erzeugt, weil dafür Lizenzierung nötig ist.

## 7. Wellen validieren

```powershell
david2exchange run --config migration.yaml --wave wave-1 --limit 10 --output ews
david2exchange verify --config migration.yaml --wave wave-1
```

`verify` prüft, dass für die Auswahl keine offenen oder fehlgeschlagenen Manifest-Items mehr existieren. Die Zielordner-Zählung per EWS ist der nächste Ausbauschritt.

## 8. Setup

```powershell
david2exchange setup --config migration.yaml
david2exchange setup --config migration.yaml --check
david2exchange setup --config migration.yaml --remove
```

Das Setup schreibt `m365.tenantId`, `clientId` und `certThumbprint` in die YAML und legt ein Allowlist-Backup `setup-backup-<datum>.json` an. Voraussetzung: Global Admin oder Application Admin + Exchange Admin, `pwsh` 7.4+.

## 9. Grenzen

- `targets` benötigt eine interaktive Exchange-Online-Anmeldung; in CI nicht testbar.
- `setup` und `targets` sind noch nicht gegen einen echten Tenant validiert.
- Das Mapping ist derzeit eindimensional: ein Quellarchiv → eine Ziel-UPN. Mehrere Quellen auf ein Ziel sind über mehrere Einträge möglich.
