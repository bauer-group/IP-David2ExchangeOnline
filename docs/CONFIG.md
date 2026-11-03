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

Nicht sicher zugeordnete Benutzer werden als Shared-Retention-Mailbox mit `<david-id>@<domain>` bzw. der Quelladresse angelegt (`createIfMissing: true`). `user\00000000` wird mit `enabled: false` aufgenommen. `--wave <name>` setzt den Migrationsblock für alle erzeugten Einträge.

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
david2exchange targets --out targets.json
david2exchange config create --config migration.yaml --targets-file targets.json --domain example.com --wave wave-1
david2exchange discover --config migration.yaml --limit 10
david2exchange run --config migration.yaml --limit 10 --dry-run
david2exchange run --config migration.yaml --limit 10 --output local
david2exchange report --config migration.yaml --out data\report.html
```

Für einen vollständigen Lauf bzw. Delta alle offenen Items:

```powershell
david2exchange run --config migration.yaml --wave wave-1 --all --output ews
```

`limits.limit` gilt, wenn kein `--limit`/`--all` angegeben wird.

## 2. Schema

```yaml
david:
  server: 192.168.0.10                 # Hostname oder IP; nur Anzeige/Report
  archiveRoot: \\192.168.0.10\David  # UNC, auch per IP; mit oder ohne \archive möglich
  progId: DVOBJAPILib.DvISEAPI         # je nach Installation
  timezone: Europe/Berlin
  logon: AUTH                          # angemeldeter Windows-Benutzer

m365:
  tenantId: ""                         # setup füllt
  clientId: ""                         # setup füllt
  certThumbprint: ""                   # setup füllt
  ewsUrl: https://outlook.office365.com/EWS/Exchange.asmx
  provenanceDomain: david.example.local
  migrationAdminUpn: admin@example.com  # Vollzugriff auf neu angelegte Postfächer

staging:
  root: .\data
  manifest: .\data\manifest.db
  logs: .\logs

limits:
  limit: 0                             # 0 = alle; >0 nur für Testläufe; --all überschreibt
  retries: 3                           # weitere Durchläufe für vorübergehend fehlgeschlagene Items
  workers: 8                           # parallele EWS-Worker (1-16)
  batchSize: 20                        # Nachrichten je CreateItem-Aufruf (1-100)
  maxBatchMegabytes: 20                # EML-Größe je Batch; ein größeres Item läuft allein

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
    include: [mail, fax, sms, voice, chat, calendar, contacts, tasks]  # alle unterstützten Typen
    excludeArchives: []                # Teilbäume: ganzes Pfadsegment oder Anzeigename, z. B. ["ADDRESS"]

  - id: unverteilt
    source:
      kind: common
      archivePath: "COMMON"
    target:
      upn: info@example.com
      type: shared
      createIfMissing: true
    include: [mail, fax, sms, voice, chat, calendar, contacts, tasks]
    excludeArchives: ["ADDRESS", "#001#Adressen"]
```

### Besonderheiten

- **`user\@me`** löst der Adapter über `Account.User.ID` auf (z. B. `user\1000C000`). Für die Migration besser den echten Pfad aus `sources` verwenden.
- **Benutzerarchiv:** `user\<Hex-ID>`; die ID ist nicht die laufende Nummer. `sources` zeigt die korrekten Pfade.
- **Unverteilt:** als **ein** Mapping mit `archivePath: COMMON` eintragen. Alle Common-Unterordner werden rekursiv unter dem Ziel abgebildet. `excludeArchives` verhindert Adress-Teilarchive.
- **Shared Mailbox:** UPN als `target.upn` eintragen. `mailboxes ensure` legt fehlende Shared Mailboxes an und setzt den Anmeldenamen auf diese UPN.
- **`enabled`:** `false` schließt den Eintrag aus. `config create` setzt es nur für `user\00000000` auf `false`; nicht zugeordnete Benutzer bleiben `true` und erhalten ein Shared-Retention-Ziel. Soll ein Benutzer nicht migriert werden, vor `run` `enabled: false` setzen. Zwei aktive Einträge dürfen nicht dieselbe Quelle haben.
- **Erneutes `config create`:** Eine vorhandene Config wird nur mit `--force` überschrieben; m365-Werte, Limits und Handänderungen gingen dabei verloren. Einzelne Zuordnungen mit `map` ändern.
- **`wave`:** Gruppiert Postfächer in Migrationsblöcke. `discover/run/verify --wave <name>` bearbeitet nur diesen Block; pro Block kann auch eine eigene Config verwendet werden.
- **`source.email`:** Pflicht für aktive Benutzerpostfächer; nur diese Adresse wird für das Mapping verwendet.
- **`target.aliases`:** zusätzliche SMTP-Adressen, die beim Anlegen/Ergänzen gesetzt werden.
- **`target.createIfMissing`:** nur für Shared Mailboxes automatisch; User-Mailboxes werden nicht automatisch lizenziert.
- **Zielordner:** Exchange-Spezialordner werden über `WellKnownFolderName` gebunden, nicht über lokalisierte Anzeigenamen. Die David-Quelle wird über den technischen Ordnerschlüssel klassifiziert (`in`, `out`, `cal`, `address`, `todo`, unter `system` zusätzlich `trash`, `spam`, `drafts`). Eigene Ordner der obersten Ebene kommen unter den Eingang; ein eigener Ordner namens „Ausgang“ wird dadurch nie zu Gesendete Elemente. Termine, Kontakte und Aufgaben aus Papierkorb, Spam und Entwürfen landen nicht in den Live-Ordnern, sondern im eigenen Stamm unter `David-Papierkorb`, `David-Spam` bzw. `David-Entwürfe`.
- **Verknüpfte Archive:** Archive, die David aus anderen Bereichen in den Baum eines Benutzers einhängt (z. B. `resource\hol`, freigegebene Kalender), werden nicht in dieses Postfach migriert; jedes wird im Log genannt. Soll ein solches Archiv migriert werden, braucht es einen eigenen Mapping-Eintrag.
- **`include`:** steuert die Typen. `mail`, `fax`, `sms`, `voice`, `chat`, `calendar`, `contacts`, `tasks` oder `all`. Fehlt der Typ, wird das Item bewusst als `skipped` geführt.
- **EML-Verhalten:** Nur Mail (Typ 2) wird über `DVEmlFromMailItem` als originale David-EML exportiert. Fax/SMS/Chat/Voice werden nicht nativ über diese Funktion exportiert, weil sie keine `MailItem`-Objekte sind und der native Aufruf den Prozess beenden kann. Bei Mail wird bei Fehlschlag eine generische EML mit Headern, Text und Anhängen erzeugt; der Fallback hängt **keinen** Provenienztext an den Body. Hat die Mail dann weder lesbaren Text noch Anhang, bleibt sie `failed`. Liefert David HTML, wird `multipart/alternative` mit `text/plain` und `text/html` erzeugt.
- **Richtung:** Bei Fax/SMS/Voice/Chat bestimmt das David-Feld `Received` Absender und Empfänger. Eingehend: Rufnummer oder Kennung als Absender, Zielpostfach als Empfänger. Ausgehend: Zielpostfach als Absender, Rufnummer als Empfänger. Rufnummern erscheinen als Anzeigename mit einer abgeleiteten `@david.local`-Adresse. Header werden RFC-konform kodiert, das Datum trägt den Offset aus `david.timezone`.
- **Voice:** wird als Mail mit der Originalaudiodatei als Anhang migriert (`VoiceItem.Content` oder David-Anhang), `Caller` wird Absender. Es wird **keine Transkription** erzeugt. Ohne Audiodatei bleibt das Item `failed`; es entsteht keine leere Nachricht. An einem echten Voice-Item noch nicht geprüft.
- **SMS/Chat:** ohne lesbaren Text und ohne Anhang bleibt das Item `failed`.
- **Fax:** liest bei **entpackten** Archiven das Original-TIFF aus `FaxItem.Image`, konvertiert es in ein mehrseitiges PDF in physisch korrekter Größe (auch bei Standardauflösung 204 x 98 dpi) und benennt es `fax-<faxnummer>-<datum>.pdf`. Datum und Lesestatus stammen aus der `Fields`-Collection, weil `FaxItem` keine eigenen Felder dafür hat. Weitere David-Anhänge bleiben zusätzlich erhalten. Wenn David keinen Betreff liefert, wird `[FAX] <faxnummer>` bzw. `[FAX]` als Betreff verwendet. Bei **gepackten** Archiven existiert der Image-Pfad nicht; das Item bleibt `failed`, auch wenn David Anhänge nennt, und es wird keine Text-EML erzeugt. Vorher Archive mit `david2exchange unpack` entpacken und die David-Bereinigung laufen lassen; siehe [UNPACK.md](UNPACK.md).
- **Ablauf:** Ein einziger STA-Thread liest David, exportiert EML und füllt eine begrenzte Queue. Bis zu `limits.workers` Worker übertragen parallel in Batches. Jedes gespeicherte Item wird über seine `ItemId` samt Elternordner und `ItemKey` geprüft; Items werden nie verschoben. Bei unbekanntem Ausgang (Timeout, Verbindungsabbruch) wird vor dem erneuten Senden im Zielordner gesucht. Details: README, Abschnitt 5.
- **Anhänge an Terminen, Kontakten und Aufgaben:** werden nicht übertragen. Solche Objekte bleiben sichtbar `failed`, statt ohne ihre Dateien als übertragen zu gelten.
- **Lesefehler:** Ein COM-Fehler gilt nie als „keine Daten“. Ein nicht lesbares Item oder Archiv hält die übrigen nicht auf, das Postfach gilt danach aber als fehlgeschlagen.
- **Speicher:** Die Queue ist begrenzt; das Postfach wird nie komplett geladen. Termine, Kontakte und Aufgaben behalten ihre Eigenschaften im Manifest, Mails nur ihre EML-Datei.
- **Staging-Aufräumen:** Nach bestätigtem Import wird die EML gelöscht; Arbeitsdateien für Anhänge schon beim Erzeugen der EML. Ist für ein Postfach nichts mehr offen, wird sein Staging-Ordner entfernt. Fehlgeschlagene Items behalten ihre EML für den nächsten Versuch.
- **Mehrere Quellen auf ein Ziel:** erlaubt; `id` muss eindeutig sein.
- **Typen:** 1 = Adresse/Kontakt, 2 = Mail, 3 = Fax, 4 = SMS, 5 = Voice, 6 = Chat/TMail, 7 = Kalender, 12 = Aufgabe. Der Lokal-Export schreibt alle unterstützten Typen; EWS erzeugt Mail, Termin, Kontakt oder Aufgabe.

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

Das Skript verbindet sich interaktiv mit Exchange Online, legt fehlende **Shared Mailboxes** an (`New-Mailbox -Shared`), setzt den Anmeldenamen über `-MicrosoftOnlineServicesID` auf die Mailadresse, ergänzt `aliases` per `Set-Mailbox` und vergibt `FullAccess` ohne Auto-Mapping an den Admin. User-Mailboxes werden nicht automatisch erzeugt, weil dafür Lizenzierung nötig ist.

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
