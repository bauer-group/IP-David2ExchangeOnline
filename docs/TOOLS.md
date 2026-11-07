# Postfach-Werkzeuge (Microsoft Graph)

Unter `tools/` liegen zwei PowerShell-Skripte für die Nacharbeit im Exchange-Online-Postfach nach dem Import. Sie sind von `david2exchange.exe` unabhängig: Sie brauchen weder `migration.yaml` noch die David-Workstation noch die App-Registrierung aus `setup` und laufen auf jedem Windows-Rechner mit Internetzugang.

| Skript | Zweck |
|---|---|
| [`Move-MessagesBeforeDate.ps1`](../tools/Move-MessagesBeforeDate.ps1) | Alle Mails vor einem Stichtag aus **einem** Ordner in einen anderen verschieben, z. B. Altbestand aus dem Posteingang in einen Unterordner |
| [`Mark-FolderTreeRead.ps1`](../tools/Mark-FolderTreeRead.ps1) | Alle ungelesenen Mails eines Ordners **inklusive aller Unterordner** als gelesen markieren |

Beide arbeiten serverseitig über Microsoft Graph. Es werden keine Mails heruntergeladen.

## 1. Voraussetzungen

- **PowerShell 7** (`pwsh`). `Move-MessagesBeforeDate.ps1` verlangt sie zwingend; `Mark-FolderTreeRead.ps1` läuft auch unter Windows PowerShell 5.1.
- Modul `Microsoft.Graph.Authentication`. Fehlt es, installieren die Skripte es für den aktuellen Benutzer aus der PowerShell Gallery.
- Anmeldung **delegiert** als der Benutzer, dem das Postfach gehört (Windows-Anmeldefenster von `Connect-MgGraph`, App „Microsoft Graph Command Line Tools“). Angefordert wird nur `Mail.ReadWrite`. Erlaubt der Tenant keine Benutzerzustimmung, muss ein Administrator einmalig zustimmen.
- Die Anmeldung bleibt zwischengespeichert, auch für neue PowerShell-Fenster. Die erste Zeile jeder Ausgabe (`Konto … | Postfach …`) zeigt, wer angemeldet ist und welches Postfach bearbeitet wird. Vor dem Wechsel zu einem anderen Benutzer `Disconnect-MgGraph` ausführen.
- Ein **eigenes PowerShell-Fenster ohne geladenes `ExchangeOnlineManagement`-Modul**. Beide Module bringen eigene MSAL-Versionen mit; im selben Prozess scheitert die Anmeldung.
- Ein fremdes Postfach (Shared Mailbox, Kollege) geht nur mit `-Mailbox <adresse>` **und** Vollzugriff auf dieses Postfach. Die Skripte fordern dann zusätzlich `Mail.ReadWrite.Shared` an.

Beide Skripte arbeiten nur im **Primärpostfach**. Ordner im Online-Archiv erreichen sie nicht; deshalb vor einer Archivierung ausführen.

## 2. Empfohlene Reihenfolge

1. Migration des Postfachs abschließen: letzter Delta-Lauf, `verify` grün. `data\manifest.db` aufbewahren (siehe Abschnitt 6).
2. `Move-MessagesBeforeDate.ps1 … -CountOnly`, dann ohne `-CountOnly` ausführen, dann erneut mit `-CountOnly` → `Zu verschieben: 0`.
3. `Mark-FolderTreeRead.ps1 … -CountOnly` auf den Zielordner, dann ausführen, dann erneut mit `-CountOnly` → `davon ungelesen: 0` (ein kleiner Rest sind Elemente, die keine Mails sind, siehe Abschnitt 5).
4. Erst danach archivieren (Archivrichtlinie, Online-Archiv).

Erst verschieben, dann markieren: So betrifft das Markieren nur den verschobenen Altbestand und nicht die aktuellen Mails im Posteingang.

## 3. Ordnerpfade

- Pfad ab dem Postfachstamm, Ebenen mit `/` getrennt, Anzeigenamen wie in Outlook, also in der Sprache des Postfachs: `"Posteingang/[BGI] BAUER GROUP IT (DE)"`, nicht `"Inbox/…"`.
- Groß- und Kleinschreibung spielen keine Rolle. Den Pfad in Anführungszeichen setzen, sobald er Leerzeichen oder Klammern enthält.
- Ordnernamen, die selbst ein `/` enthalten, lassen sich nicht ansprechen.
- Gibt es auf einer Ebene zwei gleichnamige Ordner, nimmt das Skript den ersten und warnt.
- Ausgeblendete Ordner werden nicht gefunden.
- `Move-MessagesBeforeDate.ps1` akzeptiert für `-Source` und `-TargetPath` zusätzlich die sprachunabhängigen Namen `inbox`, `sentitems`, `deleteditems`, `archive`, `drafts`, `junkemail`. `archive` ist der Ordner „Archiv“ im Primärpostfach, **nicht** das Online-Archiv. `Mark-FolderTreeRead.ps1` kennt nur Pfade.

## 4. `Move-MessagesBeforeDate.ps1`

Verschiebt alle Mails, deren Datum **streng vor** dem Stichtag liegt, aus dem Quellordner in den Zielordner. Unterordner der Quelle bleiben unberührt.

| Parameter | Pflicht | Standard | Bedeutung |
|---|---|---|---|
| `-Source` | ja | – | Quellordner: Pfad oder sprachunabhängiger Name (Abschnitt 3) |
| `-TargetPath` | ja | – | Zielordner; muss bereits existieren |
| `-Before` | ja | – | Stichtag, lokale Zeit 00:00 Uhr. **Immer im ISO-Format** `yyyy-MM-dd` angeben |
| `-DateField` | nein | `sentDateTime` bei `-Source sentitems`, sonst `receivedDateTime` | Datumsfeld für den Filter |
| `-ThrottleLimit` | nein | `4` | gleichzeitige Anfragen, 1–4 |
| `-PageSize` | nein | `400` | IDs pro Abfrage, 50–1000 |
| `-Mailbox` | nein | angemeldeter Benutzer | anderes Postfach, siehe Abschnitt 1 |
| `-CountOnly` | nein | – | nur zählen, nichts ändern |

```powershell
# Posteingang: alles vor dem 01.01.2026 in einen Unterordner
.\Move-MessagesBeforeDate.ps1 -Source inbox -TargetPath "Posteingang/[BGI] BAUER GROUP IT (DE)" -Before 2026-01-01 -CountOnly
.\Move-MessagesBeforeDate.ps1 -Source inbox -TargetPath "Posteingang/[BGI] BAUER GROUP IT (DE)" -Before 2026-01-01

# Gesendete Elemente: Datumsfeld ist automatisch sentDateTime
.\Move-MessagesBeforeDate.ps1 -Source sentitems -TargetPath "Gesendete Elemente/[BGI] BAUER GROUP IT (DE)" -Before 2025-07-01
```

Ausgabe:

```text
Konto  : max.mustermann@example.com  |  Postfach: max.mustermann@example.com
Quelle : Posteingang  (126.191 Elemente gesamt)
Ziel   : Posteingang/[BGI] BAUER GROUP IT (DE)
Filter : receivedDateTime < 2025-12-31T23:00:00Z (lokal 01.01.2026 00:00)
Zu verschieben: 126.187
Verschiebe nach Posteingang/[BGI] BAUER GROUP IT (DE) [25.600 / 126.187  |  6,4 Mails/s  |  Rest ca. 04:22  |  Fehler: 0]
```

**Stichtag prüfen.** PowerShell liest Datumsparameter kulturunabhängig. `-Before 01.07.2025` wird auch auf einem deutschen System als **7. Januar** 2025 verstanden. Die Zeile `Filter` zeigt das tatsächlich verwendete Datum; deshalb immer zuerst mit `-CountOnly` laufen lassen.

**Datumsfeld.** Der Standard `sentDateTime` gilt nur, wenn die Quelle als `sentitems` angegeben ist. Bei `-Source "Gesendete Elemente"` filtert das Skript nach `receivedDateTime`; dann `-DateField sentDateTime` setzen.

Ablauf:

- Das Skript zählt die Treffer und holt dann seitenweise die IDs aus dem Quellordner. Jede Mail wird einzeln per `POST /messages/{id}/move` verschoben, bis zu 4 gleichzeitig. Mehr erlaubt Graph pro App und Postfach nicht.
- Drosselung (429), Serverfehler (5xx) und Netzwerkfehler: bis zu 8 Versuche je Mail mit wachsender Wartezeit; `Retry-After` wird beachtet. Ist eine Mail schon weg (404), zählt sie als erledigt. Die Seitenabfragen wiederholen dieselben Fehler bis zu 10-mal.
- Das Zugriffstoken wird vor jeder Seite neu geholt. Lehnt Graph eine Mail mit 401 ab (Token abgelaufen), wird sie mit dem nächsten Token erneut versucht.
- Dauerhafte Fehler: Die Mail wird für den Rest des Laufs übersprungen; am Ende stehen Anzahl und letzter Fehler. Die Mails bleiben im Quellordner. Fünf Runden ohne Fortschritt brechen den Lauf ab.
- Durchsatz in der Praxis: etwa 4–7 Mails/s, also 4–7 Stunden für 100.000 Mails. Die rechnerische Obergrenze von etwa 16 Mails/s ergibt sich aus 10.000 Anfragen je 10 Minuten.

## 5. `Mark-FolderTreeRead.ps1`

Markiert alle ungelesenen Mails im angegebenen Ordner und in **allen** Unterordnern als gelesen.

| Parameter | Pflicht | Standard | Bedeutung |
|---|---|---|---|
| `-FolderPath` | ja | – | Startordner als Pfad (Abschnitt 3) |
| `-Mailbox` | nein | angemeldeter Benutzer | anderes Postfach, siehe Abschnitt 1 |
| `-CountOnly` | nein | – | nur zählen, nichts ändern |

```powershell
.\Mark-FolderTreeRead.ps1 -FolderPath "Posteingang/[BGI] BAUER GROUP IT (DE)" -CountOnly
.\Mark-FolderTreeRead.ps1 -FolderPath "Posteingang/[BGI] BAUER GROUP IT (DE)"
.\Mark-FolderTreeRead.ps1 -FolderPath "Gesendete Elemente"
```

Ausgabe nach einem vollständigen Lauf:

```text
Startordner: Posteingang/[BGI] BAUER GROUP IT (DE)/David-Archiv
Ordner: 775  |  Elemente: 320.120  |  davon ungelesen: 0
Fertig: 0 Nachrichten als gelesen markiert. Zur Kontrolle mit -CountOnly erneut ausführen.
```

Ablauf:

- Das Skript sammelt den ganzen Ordnerbaum ein und zeigt Ordneranzahl, Elemente und ungelesene Elemente.
- Je Ordner holt es bis zu 200 ungelesene IDs und setzt sie per JSON-Batch auf gelesen (20 Änderungen pro Batch). Das wiederholt sich, bis der Ordner keine ungelesenen Mails mehr hat.
- Drosselung (429), Serverfehler (5xx) und Netzwerkfehler werden mit Wartezeit wiederholt. Ein Ordner, der fünf Runden lang keinen Fortschritt macht, wird mit Warnung übersprungen.
- Die Zahlen in der Kopfzeile stammen aus den Ordnerzählern. Kommen während des Laufs neue Mails an, ändert sich `Elemente` zwischen zwei Läufen geringfügig.
- `davon ungelesen` zählt Elemente jeden Typs. Das Skript erreicht nur Mails (einschließlich Besprechungsanfragen). Andere ungelesene Elemente bleiben ungelesen und stehen weiter in der Kopfzeile.

## 6. Zusammenspiel mit der Migration

- **Lesestatus.** Die Migration übernimmt den Lesestatus aus David. Ungelesene David-Mails kommen also ungelesen in Exchange an; gesendete Mails werden immer als gelesen importiert. `Mark-FolderTreeRead.ps1` braucht man daher vor allem für Posteingangsordner.
- **Delta-Läufe.** Ein Delta-Lauf überspringt alles, was in `manifest.db` als `imported` steht. Verschobene Mails werden deshalb nicht erneut importiert. Neue David-Mails landen beim nächsten Delta-Lauf wieder im zugeordneten Ordner, etwa im Posteingang. Das Verschiebeskript danach erneut ausführen; es verschiebt nur, was noch im Quellordner liegt.
- **Nur bei grünem `verify` verschieben.** Einträge im Status `failed` oder `importing` können schon in Exchange liegen. Der nächste Lauf sucht sie nur im ursprünglichen Ordner und legt sie dort neu an, wenn sie inzwischen verschoben wurden. Deshalb erst verschieben, wenn `verify` grün ist, und nie, während `david2exchange run` für dieses Postfach läuft.
- **Manifest aufbewahren.** Ohne `manifest.db` überträgt ein neuer Lauf **alle** Items erneut, verschobene wie nicht verschobene: Beim ersten Versuch eines Items sucht die Migration nicht im Ziel.

## 7. Abbruch und Wiederanlauf

Beide Skripte lassen sich jederzeit mit `Strg+C` abbrechen und neu starten. Sie arbeiten nur auf dem, was noch übrig ist: Das Verschiebeskript auf dem, was noch im Quellordner liegt, das Markierskript auf dem, was noch ungelesen ist. Zur Kontrolle am Ende mit `-CountOnly` laufen lassen.

## 8. Fehlerbilder

| Meldung | Bedeutung | Maßnahme |
|---|---|---|
| `Ordner 'X' nicht gefunden` | Pfad oder Anzeigename falsch, Ordner ausgeblendet | Pfad in Outlook prüfen; ab Postfachstamm, Sprache des Postfachs, `/` als Trenner |
| `Ordnername 'X' mehrfach vorhanden` | zwei gleichnamige Ordner auf derselben Ebene | einen davon umbenennen |
| `Quelle und Ziel sind derselbe Ordner.` | `-Source` und `-TargetPath` zeigen auf denselben Ordner | Pfade prüfen |
| `Drosselung/Timeout – warte N s` | Graph drosselt oder antwortet nicht | nichts; das Skript wartet und wiederholt |
| `Keine Fortschritte mehr – Abbruch. Letzter Fehler: …` / `… übersprungen.` | fünf Runden ohne Erfolg | später neu starten; bei `401` `Disconnect-MgGraph` und neu anmelden; Berechtigungen prüfen |
| `Konto` zeigt den falschen Benutzer | zwischengespeicherte Anmeldung eines früheren Laufs | `Disconnect-MgGraph`, neu starten |
| `N Nachrichten konnten nicht verschoben werden` | dauerhafter Fehler bei einzelnen Mails; Text des letzten Fehlers steht dabei | neu starten; bleibt der Fehler, die Mails in Outlook prüfen |
| `#requires-Anweisung für Windows PowerShell 7.0` | Verschiebeskript in Windows PowerShell 5.1 gestartet | mit `pwsh` starten |
| Anmeldefehler, `Microsoft.Identity.Client` kann nicht geladen werden | `ExchangeOnlineManagement` im selben Fenster geladen | neues `pwsh`-Fenster |
| `ErrorAccessDenied` / 403 bei `-Mailbox` | kein Vollzugriff auf das fremde Postfach | Vollzugriff vergeben, neu anmelden |
