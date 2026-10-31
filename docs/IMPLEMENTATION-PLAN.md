# Umsetzungsplan david2exchange

**Professionelle Umsetzung der Migration Tobit David → Exchange Online**
**Mehrserverfähig, MIT-Open-Source, EWS-only, LiteDB, Message-ID-basiert**

> **Status:** Planungsstand 31.10.2026 (v2.3). Das Dokument beschreibt Architektur, Entscheidungen, Risiken und den ursprünglichen Zeitplan. Die **aktuelle Bedienung** steht in [README](../README.md), [CONFIG](CONFIG.md), [RELEASE](RELEASE.md) und [TEAM-PLAN](TEAM-PLAN.md); einzelne Termin- und Scope-Details können durch die Umsetzung überholt sein.

| Feld | Inhalt |
|---|---|
| Dokumenttyp | Umsetzungs-, Architektur- und Projektplan |
| Bezug | [SPECIFICATION.md](SPECIFICATION.md), v3, Stand 30.10.2026 |
| Version | 2.3 (Team-parallele Vorlage, Umsetzungsstand) |
| Vorversion | 2.2 vom 31.10.2026 |
| Datum | 31.10.2026 |
| Status | Zur Freigabe |
| Klassifizierung | Intern – vertraulich (das Produkt selbst: MIT, öffentlich) |
| Geltungsbereich | Migration beliebiger David-Installationen nach Exchange Online; Erstinstallation SRV-BAUER, David 12.00a; Lebensdauer je Einsatz ≤ 90 Tage |

> **Lesart.** Das technische Konzept bleibt die fachliche Basis. Dieses Dokument ist die **zweite, entscheidungsangepasste Vorlage**: EWS-only, LiteDB, Message-ID, MIT/Mehrserverfähigkeit und parallele Agenten sind keine offenen Fragen mehr, sondern Vorgaben. Der Plan beschreibt, wie diese Vorgaben belastbar umgesetzt werden – inklusive der Risiken, die aus ihnen folgen.

### Änderungen gegenüber v1.0

| Thema | v1.0 | v2.0 (entschieden) | Konsequenz |
|---|---|---|---|
| Ziel-API | EWS, Graph als Fallback | **EWS-only** | Kein Graph-Adapter; EWS-EOL wird als Termin- und Produktrisiko geführt |
| Manifest | SQLite empfohlen | **LiteDB** | Dokumentmodell, Checkpoint/Backup, CSV/JSON als Nachweis |
| Dedupe/Reconcile | Named Property empfohlen | **Message-ID 1:1** | Verifikation der 1:1-Übernahme und Filterbarkeit; Sonderfall Nicht-Mails |
| Produktzuschnitt | interner Einmal-Einsatz | **mehrserverfähig, MIT-OSS** | Konfiguration statt Hardcoding, Public-Repo-Hygiene, Lizenz/Doku |
| Entwicklung | ein Entwickler, 12–14 Wochen | **3 Entwickler + 4 Agenten, 2 Wochen bis RC1** | Contract-first, Integration als Engpass |
| Datenschutz | eigener Projekt-Gate | **Betreiberpflicht, keine Projektbürokratie** | Technische Schutzmaßnahmen statt Freigabeprozess |
| Kontakte | vCard primär | **vCard testen, sonst direktes Feldmapping** | `ContactMapper`, kein Kontaktverlust, T-21 |
| Ausgabe | lokaler Probe-Writer | **`IOutputTarget` + `LocalFileOutputTarget` + `EwsOutputTarget`** | Graph/PST als weitere Adapter, Pipeline unverändert |
| Umsetzungsstand | Skeleton/Probe | **LiteDB-Manifest, discover/run/dry-run/report, Setup-Skripte** | real gegen David validiert; EWS-Tenant-Test offen |
| Zeitplan | Einzelentwickler, 12–14 Wochen | **Team: 3 Devs + 4 Agenten, 2 Wochen bis RC1** | Cutover-Ziel KW 50/2026, Reserve KW 02/2027 |
| Aufwand | 134 h + 20 h + 34 h | **144 h + 22 h + 34 h** | +10 h für OSS/Mehrserverfähigkeit |

---

## 0. Management Summary

**Empfehlung: Go.** Die Architekturfragen sind entschieden; damit verschiebt sich der Fokus von „ob" auf „bis wann und wie sauber". Das ist die richtige Reihenfolge – der harte Termin ist der 1. April 2027, nicht die Technologiewahl.

**Die drei Auflagen, unter denen das Go gilt:**

1. **Tenant-Freigabe (G0).** EWS ist gesetzt, weil Veeam und andere Werkzeuge es nutzen und Graph die benötigte Funktionsparität nicht bietet. Die neue App-ID muss in die bestehende `EwsAllowedAppIDs`-Liste, `EwsEnabled` muss aktiv sein, und die Freigabe kann bis zu 24 Stunden brauchen. Das ist in den ersten drei Tagen zu prüfen, nicht nach dem Spike.
2. **Message-ID 1:1 verifizieren.** Die Entscheidung lautet: Die Message-ID wird beim MIME-Import 1:1 übernommen; die MIME-Struktur bleibt bis auf den bewussten Receipt-Header-Strip und die `X-David-*`-Provenienz unverändert. Das ist für Mail die richtige Dedupe-Basis. Zu belegen ist, dass EWS die ID tatsächlich unverändert speichert und zuverlässig nach ihr filtern kann. Für Kalender und Kontakte gibt es keine Message-ID – dort bleibt ein technischer Reconcile-Schlüssel unvermeidlich, sonst entstehen nach einem Absturz Duplikate.
3. **LiteDB betriebssicher machen.** LiteDB spart Entwicklungszeit, ist aber ein proprietäres Dateiformat. Der Nachweis darf deshalb nicht allein in der `.db` liegen: Vor jedem Lauf ein Checkpoint/Backup, am Ende jedes Laufs und beim Cutover ein CSV-/JSON-Export, dazu eine dokumentierte Schema-Version und ein Repair-Pfad. Dann ist LiteDB eine gute Wahl.

**Termin:** Die Einzelentwickler-Kurve ist ersetzt. Mit **3 Entwicklern + 4 Coding-Agenten** in vier Streams ist der Zielkorridor: **RC1 in KW 47 (20.11.2026)**, **Pilot KW 48 (27.11.)**, **Cutover KW 50 (07.–11.12.2026)**, Reserve KW 51 und KW 02/2027. Der Gesamtaufwand bleibt 200 h; die Parallelität verkürzt den Kalender, nicht Abnahme und Cutover. Wenn ein Gate rutscht, verschiebt sich der Cutover in KW 02 – der harte Termin 01.04.2027 bleibt mit Puffer erreichbar.

**Open Source:** Das Tool wird unter MIT veröffentlicht (die `LICENSE` liegt bereits vor). Es enthält keine internen Hostnamen, Domains, Postfachnamen oder Pfade; alle installationsspezifischen Werte kommen aus `migration.yaml`. Die öffentliche Doku ist englisch, die Laufzeitlogs und Reports bleiben deutsch (Zielgruppe David-Markt).

**Entscheidungsstand:**

| ID | Entscheidung | Status | Termin |
|---|---|---|---|
| D-01 | Manifest: **LiteDB** | entschieden | – |
| D-02 | Target: **EWS-only**, kein Graph | entschieden | – |
| D-03 | Dedupe/Reconcile: **Message-ID 1:1** | entschieden | – |
| D-04 | **Mehrserverfähig, MIT-OSS** | entschieden | – |
| D-05 | **Parallele Agenten/Branches** | entschieden | – |
| D-06 | Manifest-PK `(mailbox_id, archive_scope, david_id)` | empfohlen | nach S2 |
| D-07 | Parallelität: 1 Postfach starten | empfohlen | nach S0 |
| D-08 | Packaging: self-contained x86 | empfohlen | 18.11.2026 |
| D-09 | Application Access Policy verpflichtend | empfohlen | 11.11.2026 |
| D-10 | `EwsEnabled`-Opt-in | empfohlen | 04.11.2026 |
| D-11 | Cutover KW 50/2026, Reserve KW 51/KW 02 | empfohlen | 20.11.2026 |
| D-12 | Kontakte: vCard testen, sonst **direktes Feldmapping** | entschieden | – |
| D-13 | Aufwand 144 h + 22 h Puffer, 4 Streams, 3 Devs + 4 Agenten | Planwert | 04.11.2026 |

---

## 1. Auftrag, Scope und Erfolgskriterien

### 1.1 Zielbild

Ein einmalig eingesetztes, aber **konfigurationsgetriebenes** Konsolentool liest alle freigegebenen David-Archive (Benutzer, Archivkonten, Unverteilt) über DvApi32 aus, normalisiert die Inhalte und schreibt sie per EWS app-only in bestehende Exchange-Online-Postfächer. Es ist jederzeit abbrechbar, setzt exakt fort, erzeugt keine Duplikate, löst keine Lesebestätigungen oder Einladungen aus und hinterlässt einen prüfbaren Migrationsnachweis.

Das Tool ist **nicht** auf SRV-BAUER zugeschnitten: David-Server, Archivwurzel, ProgID, Zeitzone, Ziel-UPNs, Provenienz-Domain und Ordner-Mapping stehen in der Konfiguration. Andere David-Installationen sollen ohne Codeänderung migrierbar sein, solange sie die dokumentierte DvApi32-Schnittstelle anbieten. Veröffentlicht wird unter MIT, ohne Gewährleistung, mit klarem EWS-EOL-Hinweis.

### 1.2 Scope-Matrix

| Objekt | Quelle | Ziel | Im Scope | Anmerkung |
|---|---|---|---|---|
| E-Mails | David-Mail-Items | Postfachordner | Ja | EML, Header-Bereinigung, Message-ID 1:1 |
| Faxe | David-Fax-Items | Postfachordner | Ja | synthetische Mail + Bildanhang |
| SMS | David-SMS-Items | Postfachordner | Ja | synthetische Mail + Text |
| Chats | David-Chat-Items | Postfachordner | Ja | Klartextverlauf, optional Original |
| Sprachnachrichten | David-Voice-Items | Postfachordner | Ja | synthetische Mail + Audio |
| Kalender | David-Termine | Kalenderordner | Ja | Einzeltermine, Serien expandiert |
| Kontakte | David-Kontakte | Kontaktordner | Ja | vCard nur falls EWS es trägt; sonst verbindliches Feldmapping |
| Ordnerstruktur | David-Archive | Postfachordner | Ja | inkl. leerer Ordner, sanitisiert |
| Lesestatus | David-Status | EWS `IsRead` | Ja | nur konfigurierbares Zeitfenster |
| Berechtigungen, Regeln, Signaturen, Abwesenheit | David | – | Nein | im Report als „nicht migriert" ausweisen |
| Verteilerlisten, Aufgaben, Notizen, Ressourcen | David | – | Nein | sofern vorhanden, explizit dokumentieren |
| Löschen/Verschieben im Ziel | – | – | Nein | keine Synchronisation nach Cutover |
| Postfach-/Lizenzanlage | – | – | Nein | erfolgt vorher durch Administration |
| Exchange on-prem als Ziel | – | – | Nein | EWS-Auth wäre anders; mögliche spätere Erweiterung |

### 1.3 Produkt- und OSS-Scope

| Thema | Festlegung |
|---|---|
| Lizenz | MIT, `LICENSE` vorhanden (Copyright 2026 BAUER GROUP) |
| Öffentliches Repo | README (EN), `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`, `CHANGELOG.md`, Issue-/PR-Templates, CI |
| Keine Interna | keine Hostnamen, Domains, UPNs, Archivpfade, Tenant-IDs, Zertifikate, Logs oder Manifeste im Repo |
| Konfiguration | `config/migration.example.yaml` mit Platzhaltern (`example.com`, `david.example.local`) |
| Kompatibilität | David 12.x verifiziert; andere Versionen über spätes Binden und konfigurierbare ProgID |
| Doku-Sprache | öffentliches README/Doku Englisch; CLI-/Log-/Report-Texte Deutsch (David-Markt) |
| Support | Community, keine SLA; Security-Policy für Meldungen |
| EOL-Hinweis | Exchange Online EWS endet am 01.04.2027; das Tool ist ein Migrationswerkzeug, kein Dauerprodukt |

### 1.4 Annahmen

| ID | Annahme | Prüfpunkt |
|---|---|---|
| A-01 | Der Tenant erlaubt EWS weiterhin für eine neue App-ID (Allowlist) | Gate G0 |
| A-02 | Zielpostfächer existieren, sind lizenziert und ausreichend groß | `discover`/Kapazitätscheck |
| A-03 | Der Migrationsbenutzer hat David-Rechte auf alle Quellarchive | `discover` |
| A-04 | DvApi32 ist 32-Bit und benötigt eine interaktive Windows-Sitzung | Spike S0/S2 |
| A-05 | Quellvolumen und Postfachanzahl sind bekannt und passen in die Zielquota | Kapazitätscheck |
| A-06 | Es gibt keine konkurrierenden Änderungen an der Allowlist während des Setups | Betriebsregel |
| A-07 | Administratoren mit Global Admin/Exchange Admin sind verfügbar | Setup |
| A-08 | Andere David-Installationen nutzen eine kompatible DvApi32-Typelib | `doctor`, Testmatrix |
| A-09 | Die Migration erfolgt in einem Wartungsfenster ohne Änderungen an David | Cutover-Plan |
| A-10 | EWS bleibt mindestens bis zum Cutover erreichbar | täglicher Scream-Test |

### 1.5 Erfolgs- und Abnahmekriterien

| ID | Kriterium | Messung |
|---|---|---|
| E-01 | Jedes Quell-Item ist genau einem Zustand zugeordnet | `Quelle = importiert + failed + skipped + ausgeschlossen`; unaccounted = 0 |
| E-02 | Keine Duplikate im Ziel | Stichprobe + Zählung + Reconcile-Test nach Kill |
| E-03 | Keine Lesebestätigung, keine Einladung, kein Versand | S1/S3 + Outlook-Prüfung |
| E-04 | Wiederaufnahme nach Abbruch | Kill-Test, maximal ein Batch Wiederholung |
| E-05 | Nachweisbarkeit | LiteDB + CSV/JSON, SHA-256, Tool-Version, Config-Hash |
| E-06 | Decommissioning vollständig | App, Consent, Cert, Allowlist-Eintrag entfernt; Backup vorhanden |
| E-07 | Termingerecht | Cutover vor 31.03.2027, Puffer ≥ 4 Wochen |
| E-08 | Keine PII in Logs | Log-Review, Test auf Inhalte |
| E-09 | Mehrserverfähig | zweite Konfiguration ohne Codeänderung, `doctor` grün |
| E-10 | OSS-fähig | Clean-Clone-Build, keine Interna, MIT-Header, öffentliche Doku |
| E-11 | Message-ID 1:1 | Quelle und Ziel identisch, inkl. Klammern/Case |
| E-12 | Bedienbarkeit | Ein Operator kann Run/Resume/Report ohne Entwicklerhilfe |

---

## 2. Analyse des technischen Konzepts

### 2.1 Bewertung

| Dimension | Bewertung | Begründung |
|---|---|---|
| Zielklarheit und Scope | 5/5 | Klare Leitplanken, explizite Nicht-Ziele, ein Target-Adapter |
| Architektur | 4/5 | Schichten sauber; COM-Nebenläufigkeit und Identität unterschätzt |
| Datenmodell | 4/5 | Zustandsautomat stark; LiteDB braucht Backup/Export-Disziplin |
| Fehlertoleranz | 4/5 | Batch-Splitting, Retry, Skip, Resume gut; Reconcile für Nicht-Mails offen |
| Testbarkeit | 4/5 | Guter Abnahmekatalog; Message-ID- und LiteDB-Tests fehlten |
| Sicherheit | 4/5 | App-only und Cert gut; Access Policy war optional |
| Betrieb | 4/5 | Runbook und Delta vorhanden; DvApi-Sitzung und Quota sind Risiken |
| Zeitplan | 4/5 | Mit parallelen Streams und frühem Cutover beherrschbar |
| Aufwand | 4/5 | 144 h sind realistisch, nicht 37 h |
| **Gesamt** | **4,1/5** | **Go mit klaren Auflagen** |

### 2.2 Stärken

- **Ein Zustandsautomat statt Hoffnung.** `discovered → exported → importing → imported` mit Einzel-Commits und explizitem `importing`-Reconcile ist die richtige Grundidee für ein abbrechbares Tool.
- **Das Manifest als Wahrheit.** Keine versteckten Checkpoint-Dateien, keine zweite Zustandsquelle; mit LiteDB bleibt es kompakt, mit CSV/JSON wird es nachweisbar.
- **Delta ohne Wasserzeichen.** Vollständiger Metadaten-Scan plus Diff ist robust gegen verpasste Läufe und Uhrzeitprobleme.
- **Sicherheitsgrundsatz richtig:** app-only mit Zertifikat, kein Client-Secret, Allowlist-Backup vor Änderung.
- **Testkatalog mit harten Kriterien.** Besonders gut: Kill-Test, Receipt-Test, Dry-Run, `--limit`, Skip/Unskip, Throttling.
- **Realistische Nicht-Ziele.** Kein GUI, keine bidirektionale Synchronisation, kein Graph – das schützt vor Scope Creep.
- **Betriebsbild vorhanden.** Initial, Delta, Cutover, Abbau sind beschrieben.
- **Gute Basis für OSS:** Die Konfiguration ist bereits weitgehend datengetrieben; es fehlt vor allem die Disziplin, Interna nicht ins Repo zu schreiben.

### 2.3 Befunde

Schwere: **K** = kritisch, **H** = hoch, **M** = mittel, **N** = niedrig.

| ID | Schwere | Befund | Auswirkung | Maßnahme |
|---|---|---|---|---|
| F-01 | H | EWS ist gesetzt, aber der Tenant muss die neue App-ID freigeben; EWS endet am 01.04.2027. | Termin- und Machbarkeitsrisiko, nicht Architekturrisiko | G0: Allowlist, `EwsEnabled`, Canary; Cutover KW 50 |
| F-02 | H | `setup` verifiziert nur 10 Minuten (1.5); Allowlist-Änderungen brauchen laut Fachbeiträgen bis zu 24 h. | falsch-negatives Setup, unnötige Fehlersuche | 30 min aktiv prüfen, dann „pending" mit Exit-Code 3; `--check` erneut |
| F-03 | H | `setup` setzt `EwsEnabled=true` (1.3/1.5) ohne expliziten Opt-in. | tenantweite Sicherheitsentscheidung als Nebenwirkung | separater Schalter `--enable-ews`, Warnung, Audit-Log, Freigabe dokumentieren |
| F-04 | M | LiteDB ist entschieden, aber das Konzept nennt keine Durability-/Retention-Regeln. | Zustandsverlust bei Crash, Nachweis nur im proprietären Format | Checkpoint/Backup, CSV/JSON-Export, Schema-Version, Repair-Pfad |
| F-05 | H | PK `(mailbox_id, src_path, david_id)`; ein verschobenes Item bekommt einen neuen Pfad und damit einen neuen PK. | Reimport nach Verschieben, Duplikate, falsche Zähler | stabile Identität `(mailbox_id, archive_scope, david_id)` prüfen; Pfad als Attribut |
| F-06 | H | Message-ID 1:1 ist entschieden, aber EWS-Verhalten (Speicherung, Filterbarkeit, Normalisierung) ist nicht belegt. | Dedupe/Reconcile funktioniert nicht wie geplant | S4: ID nach Import binden und vergleichen; `FindItems` testen |
| F-07 | H | `importing`-Reconcile über `message_id` (10.3); Kalender/Kontakte haben keine Message-ID. | Duplikate nach Absturz bei Terminen/Kontakten | technischer Reconcile-Schlüssel nur für Nicht-Mails; Test T-17 |
| F-08 | H | `parallelMailboxes: 2` in einem Prozess mit COM; DvApi ist wahrscheinlich STA. | Datenrennen, COM-Fehler, undefinierte Zustände | mit 1 starten; dedizierte STA-Threads nur nach Messung |
| F-09 | M | `COMReference` mit Null-GUID Platzhalter; Build braucht registrierte Typelib. | CI ohne David-Client baut nicht; OSS-Beitragende scheitern | Late Binding + `IDavidApi`-Adapter; optional Interop-Assembly vendoren |
| F-10 | H | EWS-Managed-API-Paket 1.1.3 (2018, Drittanbieter-Port); `OAuthCredentials` und .NET 10/x86 nicht verifiziert. | kein Support, Sicherheitslücken, Auth-Überraschung | exakt pinnen, S0, Quellcode archivieren, SOAP-Fallback bewerten |
| F-11 | M | Graph- und Exchange-Online-Module laufen in einem `pwsh`-Kindprozess (1.5). | Assembly-Konflikte, schwer reproduzierbare Setup-Fehler | zwei getrennte Prozesse (Graph, EXO), C# orchestriert JSON |
| F-12 | M | Zertifikat 6 Monate, Projekt inkl. Delta länger; Renewal fehlt. | Ablauf mitten im Betrieb, Import bricht auf | `setup --renew-cert`, Ablaufwarnung < 30 Tage, Key-Rollover |
| F-13 | M | Dry-Run soll Items nicht verändern (9), `run` aktualisiert aber `last_seen_run` (10.1). | Abnahmekriterium „Dump identisch" (14) ist unerfüllbar | Dry-Run schreibt keine Item-Zustände; Vergleich nur der Zustandsfelder |
| F-14 | M | `--skip-dedupe-check` ohne Guard auf nicht-leeres Ziel. | Duplikate bei Wiederverwendung eines Postfachs | Guard: Zielordner leer oder `--force`; Warnung im Report |
| F-15 | H | Kein Backup-/Restore-Konzept für das Manifest. | bei Korruption ist der Nachweis und die Wiederaufnahme weg | Checkpoint + Datei-Backup vor jedem Lauf, 7 Tage Rotation, Restore-Test |
| F-16 | H | Kein Lock gegen parallele `run`-Instanzen. | zwei Prozesse schreiben denselben Zustand | Lock-Datei/DB-Lock mit PID/Host, `--force-unlock` nur bewusst |
| F-17 | H | 120 MB Einzelitem, Base64, EWS-Kopien, x86-Prozess, 2 parallele Postfächer. | `OutOfMemoryException`, Lauf bricht mitten im Batch | Item-Limit real messen (50–80 MB), große Items seriell |
| F-18 | N | Ordner-Sanitizing nennt nur `/`, `\`, Steuerzeichen; `FindFolders` mit `FolderView(2)` ohne Paging. | falsche Ordnerzuordnung, Kollisionen | vollständige Zeichenliste, Paging, exakter Vergleich |
| F-19 | M | Kalender: Zeitzone/DST und Serienexpansionsgrenzen sind knapp beschrieben. | Termine um eine Stunde verschoben, Serien unvollständig | `DateTimeKind.Unspecified`, Windows-TZ-ID, DST-Test Januar/Juli |
| F-20 | M | vCard-Import via EWS `Contact.MimeContent` ist nicht garantiert; Felder können verloren gehen. | Kontakte fehlen oder verlieren Felder | S3 testet vCard; bei Fehler greift das direkte Feldmapping, kein Kontakt wird verworfen |
| F-21 | M | Header-Strip und `X-David-*` verändern die MIME-Struktur. | DKIM/S-MIME-Signaturen werden ungültig | S/MIME erkennen, unverändert importieren, dokumentieren |
| F-22 | H | Kapazitätsprüfung fehlt; nur Warnung > 40 GB auf Shared Mailbox. | Import läuft in Quota, spät und teuer | Quota je Ziel ermitteln, Ampel im `discover`, Go/No-Go je Postfach |
| F-23 | M | Report-Zähler „Quelle = importiert + failed + skipped + ausgeschlossen" ist nicht definiert; CSV ohne Injection-Schutz; Subjekte = PII. | falsche Abnahme, Excel-Formelrisiko | Zählerformel festlegen, CSV escapen, PII-Toggle |
| F-24 | M | Staging-Pfad kann 260 Zeichen überschreiten; keine Checksumme. | Export/Import scheitert oder liest beschädigte Datei | Pfad-Hash/Truncation, `\\?\`, SHA-256 im Manifest |
| F-25 | H | DvApi-AUTH braucht angemeldete Sitzung; geplanter Task „nur bei Anmeldung" ist fragil. | Delta-Läufe fallen aus, Cutover verzögert sich | dedizierte Migrations-VM, definierter Run-User, Monitoring |
| F-26 | M | App-Idempotenz nur über `DisplayName`. | bei Duplikaten wird die falsche App aktualisiert | ObjectId in Config/Backup speichern, Tag `david2exchange` |
| F-27 | M | 37 h sind Best Case ohne Härtung, Testautomatisierung, OSS und Betrieb. | Zeitplan- und Budgetüberschreitung | 144 h + 22 h Puffer + 34 h Betrieb; parallele Streams |
| F-28 | M | Abnahme deckt Quota, Disk voll, LiteDB-Korruption, Long Path, S/MIME, Message-ID-Identität, Kontakt-Fallback, Ausgabeport nicht ab. | falsche Sicherheit, späte Feldfehler | Testfälle T-01…T-22 ergänzen |
| F-29 | H | Interne Identifier (SRV-BAUER, `de.bauer-group.com`, konkrete Pfade/UPNs) stehen in Spezifikation und Beispielen. | OSS-Veröffentlichung würde Interna leaken | generische Konfiguration, Scrubbing, Public-Repo-Check |
| F-30 | H | Application Access Policy ist optional (1.2), `full_access_as_app` gilt sonst tenantweit. | überbreiter Zugriff, Security-Audit-Befund | D-09: Policy verpflichtend, Ausnahme nur schriftlich |
| F-31 | N | `skip --item <david_id>` ohne Pfad ist bei gleicher ID in mehreren Ordnern mehrdeutig. | falsches Item wird übersprungen | optional `--path`, bei Mehrdeutigkeit abbrechen |
| F-32 | M | Floating-Versionen (`1.1.*`, `2.*`, `4.*`, `10.*`) im csproj. | nicht reproduzierbare Builds | zentrale Paketverwaltung, exakte Versionen, Lockfile |
| F-33 | N | Proxy/TLS-Inspection/Netzwerkpfad nicht konfigurierbar. | EWS scheitert in segmentierten Netzen | `network.proxy`, Timeout, Diagnosekommando `doctor` |
| F-34 | M | Staging/Logs werden nach Cutover nicht aufgeräumt; Retention unklar. | PII bleibt liegen | `cleanup`-Kommando, Retention-Konzept, Löschprotokoll |
| F-35 | M | Mehrserverfähigkeit ist behauptet, aber nicht konstruktiv abgesichert (ProgID, Archivlayout, Typelib-Version). | andere David-Server scheitern an Kleinigkeiten | `david.progId`, `source.kind`, `discover --list-archives`, Testmatrix |
| F-36 | M | Parallele Agenten erzeugen Merge-Konflikte und inkonsistente Verträge. | Integration wird zum Engpass | Contract-first, Streams, Integrator, tägliche Merges |
| F-37 | H | EWS-EOL ist entschieden, aber OSS-Nutzer und Betrieb brauchen eine klare Aussage zur Nutzungsdauer. | Tool wird nach 01.04.2027 unbrauchbar, Erwartungsmanagement fehlt | EOL-Hinweis in README/Release, Cutover-Termin, keine Dauerwartung |

### 2.4 Kernbefunde im Detail

#### 2.4.1 EWS-only und das End-of-Life-Fenster (F-01, F-02, F-03, F-37)

Die Entscheidung ist bewusst: EWS bleibt der Zielweg, weil Veeam und andere Werkzeuge es im Tenant nutzen und Graph die benötigten Funktionen (u. a. MIME-1:1, Nicht-Mail-Typen, volle Ordner-/Item-Semantik) nicht vollständig abdeckt. Das ist konsistent – aber es macht den Termin zur kritischen Größe.

Seit dem **1. Oktober 2026** wird EWS in Exchange Online global deaktiviert; Ausnahmen gelten nur für App-IDs in `EwsAllowedAppIDs`, endgültig Schluss ist am **1. April 2027**. Fachbeiträge berichten, dass die Liste bis Ende August 2026 stehen musste, um der automatischen Sperre zu entgehen, und dass Änderungen bis zu **24 Stunden** brauchen. Für den Tenant ist die Liste wegen Veeam wahrscheinlich bereits vorhanden – die neue App-ID muss aber trotzdem hinein, und `EwsEnabled` muss aktiv sein.

**Konsequenz:** G0 bleibt das erste Gate. Der Canary-Test ist ein `Folder.Bind(MsgFolderRoot)` mit der neuen App-ID, nicht nur ein Token-Abruf. Der Cutover-Zielkorridor ist KW 50/2026, Reserve KW 51 und KW 02/2027. Für OSS-Nutzer steht der EOL-Hinweis in README und Release Notes; das Tool ist kein Dauerprodukt.

#### 2.4.2 Message-ID als Idempotenz-Basis (F-05, F-06, F-07)

Die Entscheidung lautet: Die Message-ID wird 1:1 übernommen; die MIME-Struktur bleibt bis auf den Receipt-Header-Strip und die `X-David-*`-Provenienz unverändert. Damit ist die Dedupe-Basis für Mail klar. Drei Dinge müssen trotzdem belegt werden:

1. **Speicherung:** Nach dem Import wird das Item gebunden und `InternetMessageId` mit der Quelle verglichen – inklusive Klammern, Groß-/Kleinschreibung und Whitespace. Abweichungen werden normalisiert nur für den Vergleich, nicht im Header.
2. **Filterbarkeit:** `FindItems` mit `IsEqualTo(InternetMessageId, ...)` muss zuverlässig treffen. Falls EWS hier einschränkt, ist das ein echtes Problem für Dedupe und Reconcile – dann muss auf `PidTagInternetMessageId` (0x1035) ausgewichen werden, bevor der Bau beginnt.
3. **Nicht-Mails:** Kalender und Kontakte haben keine Message-ID. Für sie bleibt ein technischer Reconcile-Schlüssel unvermeidlich: entweder eine Extended Property `david2exchange.ItemKey` oder – als schwächerer Notnagel – Betreff + Datum + Zielordner. Das ist kein Widerspruch zur Message-ID-Entscheidung, sondern die einzige Möglichkeit, `importing` nach einem Absturz auch dort aufzulösen.

Für synthetische Nachrichten (Fax, SMS, Chat, Voice) wird eine deterministische Message-ID aus Mailbox, Archiv und David-ID erzeugt. Sie ist über Läufe stabil und damit genau so dedupe-fähig wie eine echte Mail-ID.

**Manifest-PK:** `(mailbox_id, archive_scope, david_id)` statt `(mailbox_id, src_path, david_id)`. `archive_scope` ist die Archivwurzel (z. B. `user\1000C000`), nicht der Item-Ordner. Damit erzeugt ein Move innerhalb des Archivs kein Duplikat; `src_path` wird aktualisiert.

#### 2.4.3 Nebenläufigkeit und Speicher (F-08, F-17)

`parallelMailboxes: 2` klingt harmlos, ist aber mit 32-Bit-COM und einem Prozess ein Risiko:

- COM-Objekte haben Apartment-Affinität. Ein `DavidAPIClass`-Objekt darf nicht von zwei Threads gleichzeitig benutzt werden.
- Wenn DvApi globalen Zustand hält (Logon, Archiv-Cache), ist selbst je Thread ein eigenes Objekt nicht sicher.
- 120-MB-Items werden im EWS-Adapter mehrfach kopiert (byte[], Base64, XML). Bei zwei Postfächern parallel kann ein x86-Prozess an die 2-GB-Grenze stoßen.

**Empfehlung:** Start mit `parallelMailboxes: 1`; David-Zugriff auf einem dedizierten STA-Thread; Import parallel nur, wenn Messungen zeigen, dass DvApi und Speicher das tragen. Item-Limit zunächst 50 MB, dann empirisch anheben. Große Items erhalten einen seriellen Pfad. `OutOfMemoryException` ist ein Umgebungsfehler, kein Item-Fehler – der Lauf muss sauber stoppen und wiederaufnehmen können.

#### 2.4.4 Setup und Betrieb (F-10, F-11, F-12, F-25)

Das Setup läuft einmal und wird danach nie wieder getestet – genau deshalb braucht es Sorgfalt:

- **Graph und EXO in getrennten Prozessen.** Beide Module laden unterschiedliche Versionen gemeinsamer Assemblies; ein Prozess ist ein bekannter Stolperstein. Der C#-Wrapper startet zwei `pwsh`-Prozesse und übergibt JSON. Graph wird nur für die App-Registrierung genutzt, nicht für die Migration.
- **Zertifikatslebenszyklus.** 6 Monate sind für den Bau okay, für den Betrieb knapp. `setup --check` prüft den Ablauf, `setup --renew-cert` existiert; der Key-Rollover darf die App nicht duplizieren.
- **DvApi-Sitzung.** Ein geplanter Task „nur bei Anmeldung" ist betrieblich fragil. Besser: dedizierte Migrations-VM mit definiertem Run-User, kontrolliertem Autologon, BitLocker, restricted ACL und Monitoring. Wenn das nicht genehmigungsfähig ist, startet ein Operator den Delta-Lauf manuell.

#### 2.4.5 Datenqualität und Randfälle (F-19, F-20, F-21, F-24)

Die Migration scheitert nicht an den 95 % normalen Mails, sondern an den 5 %: S/MIME-signierte Mails, Termine mit DST-Wechsel, Kontakte mit Foto, 300 Zeichen lange Ordnerpfade, 0-Byte-Anhänge, eingebettete Nachrichten, TNEF. Der Plan ergänzt gezielte Testfälle und eine „Quarantäne"-Liste im Report, damit nichts stillschweigend verschwindet.

**Kontakte konkret:** vCard wird in S3 getestet. Wenn EWS `Contact.MimeContent` nicht annimmt oder dabei Felder verliert, wird **jedes Feld direkt gemappt**: Anzeigename, Vorname, Nachname, Firma, Abteilung, Position, bis zu drei E-Mail-Adressen, Telefonnummern (geschäftlich, mobil, privat, Fax), zwei Adressen, Notizen und `FileAs`. Nicht abbildbare David-Felder werden als Text an den Body angehängt; ein Kontakt wird nie allein wegen des vCard-Formats verworfen.

#### 2.4.6 Nachweis, LiteDB und Sicherheit (F-04, F-15, F-16, F-23, F-30)

Das Manifest ist der Arbeitszustand, nicht der einzige Nachweis. LiteDB ist bequem, aber proprietär; deshalb gilt:

- **Checkpoint + Backup** vor jedem Lauf, 7 Tage Rotation, Restore getestet.
- **CSV/JSON-Export** am Ende jedes Laufs und beim Cutover; SHA-256 über den Export.
- **Schema-Version** in `meta`, Tool-Version, Config-Hash, Run-Args, Host.
- **Lock** gegen parallele Läufe; `--force-unlock` nur bewusst.
- **PII-Schutz:** keine Inhalte in Logs, PII-Toggle im Report, Staging verschlüsselt und nach Cutover gelöscht.

`full_access_as_app` ohne Application Access Policy ist ein tenantweiter Zugriff auf alle Postfächer. Für ein Migrationstool ist das nicht angemessen; die Policy gehört in den Produktionsstandard.

#### 2.4.7 Erste DvApi-Probe (31.10.2026)

Der Datenzugriff wurde auf einem Rechner mit installiertem David-Client real getestet. Ergebnis: **Logon, Archivbaum, Benutzerarchiv, Unverteilt und EML-Export funktionieren.** Wichtige Korrekturen am Konzept:

- `user\00000003` ist kein gültiger Pfad. Benutzerarchive heißen `user\<Hex-ID>` (z. B. `user\1000C000`). `user\@me` löst über `Account.User.ID` auf.
- `GetArchive(path)` kann trotz vorhandener Rechte `Insufficient rights` liefern; der Weg über `Account.ArchiveRoot` → `GetSubArchive` ist robuster.
- `common\unverteilt` enthält Adress-Teilarchive. **Typ 1 = Adresse, Typ 2 = Mail, Typ 7 = Kalender**; für Mail `--type 2` verwenden.
- `StatusTime`/`SendTime` sind die Datumsfelder; `Received` ist ein Bool, `Viewed` der Lesestatus.
- `DVEmlFromMailItem` liefert eine gültige EML inklusive `Message-ID`.

Konsequenz: `@me` wird zum portablen Platzhalter, das Typ-Mapping wird in S2 erweitert, und die Befunde stehen in `docs/SPIKE.md`.

#### 2.4.8 Open Source und Mehrserverfähigkeit (F-29, F-35, F-36)

Das Tool soll anderen David-Installationen helfen. Dafür reicht es nicht, „konfigurierbar" zu sagen:

- **Keine Interna** in Code, Beispielen, Tests, Issues oder Doku. `SRV-BAUER`, `de.bauer-group.com`, konkrete UPNs und Pfade werden durch `example.com`/`david.example.local` ersetzt.
- **Keine Build-Abhängigkeit** von einer installierten David-Typelib. Spätes Binden über `Type.GetTypeFromProgID` und eine konfigurierbare ProgID; die Interop-Assembly ist optional.
- **Keine Annahme** über Archivlayouts. `source.kind` (`user`/`common`), `archivePath`, `folderMap` und `excludeArchives` kommen aus der Konfiguration; `discover --list-archives` hilft beim Einstieg.
- **Testmatrix:** mindestens die eigene Installation plus eine zweite David-Umgebung oder ein aufgezeichnetes Fixture; unterschiedliche Typelib-Versionen werden dokumentiert.
- **Public-Repo-Hygiene:** `SECURITY.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, DCO/CLA nicht nötig, aber klare Merge-Regeln; keine Tenant-Daten in Issues.

### 2.5 Offene Entscheidungen

| ID | Entscheidung | Optionen | Empfehlung | Begründung | Frist |
|---|---|---|---|---|---|
| D-06 | Manifest-PK | pfadabhängig / stabil | `(mailbox_id, archive_scope, david_id)` | Moves dürfen keine Duplikate erzeugen; `_ID` ist nur je Archiv garantiert | nach S2 |
| D-07 | Parallelität | 1 / 2 Postfächer | 1 starten, 2 nur nach Messung | COM-STA + x86-Speicher | nach S0 |
| D-08 | Packaging | framework-dependent / self-contained | self-contained win-x86, single-file, signiert | OSS-Nutzer sollen ohne Runtime-Installation starten | 18.11.2026 |
| D-09 | Access Policy | optional / verpflichtend | verpflichtend | Least Privilege, Auditierbarkeit | 11.11.2026 |
| D-10 | `EwsEnabled` | automatisch / Opt-in | Opt-in mit Warnung und Log | tenantweite Auswirkung | 04.11.2026 |
| D-11 | Cutover | KW 50/2026 / KW 02/2027 | KW 50, Reserve KW 51/KW 02 | Team-Kurve; Puffer bis 01.04.2027 | 20.11.2026 |

**D-12 (Kontakte) ist entschieden:** vCard wird in S3 getestet. Wenn EWS `Contact.MimeContent` nicht trägt oder Felder verliert, wird direkt gemappt – vollständig, ohne Kontaktverlust (siehe 2.4.5).

Bereits entschieden: **D-01 LiteDB**, **D-02 EWS-only**, **D-03 Message-ID**, **D-04 MIT/Mehrserver**, **D-05 parallele Agenten**, **D-12 Kontakt-Fallback**.

---

## 3. Zielarchitektur und Technik

### 3.1 Architekturüberblick

```
┌─────────────────────────────────────────────────────────────────────┐
│                        david2exchange.exe (x86)                     │
│                                                                     │
│  CLI (System.CommandLine)                                           │
│    setup | doctor | discover | run | skip | unskip | reset |        │
│    report | backup | cleanup                                       │
│                                                                     │
│  Application                                                        │
│    ┌──────────────┐  ┌──────────────┐  ┌─────────────────────────┐  │
│    │ Discover     │  │ Migration    │  │ Reporting               │  │
│    │ UseCase      │  │ Pipeline     │  │ UseCase                 │  │
│    └──────┬───────┘  └──────┬───────┘  └───────────┬─────────────┘  │
│           │                 │                      │                │
│  Domain   │  Item, Folder, ItemState, Run, SkipReason, Provenance   │
│           │                 │                      │                │
│  Ports    │  IDavidSource   │  IOutputTarget       │  IManifest      │
│           │  IStagingStore  │  IClock, IProgress   │  IReportWriter  │
│           │                 │                      │                │
│  Infra    ▼                 ▼                      ▼                │
│    ┌──────────────┐  ┌──────────────┐  ┌─────────────────────────┐  │
│    │ DvApi32      │  │ EWS          │  │ LiteDB Manifest         │  │
│    │ (COM, STA)   │  │ (Managed API)│  │ (single writer)         │  │
│    └──────────────┘  └──────────────┘  └─────────────────────────┘  │
│                                                                     │
│  Setup: pwsh child process 1 (Graph) ─┐                             │
│         pwsh child process 2 (EXO)  ──┼─► JSON result ─► Verify EWS │
│         certificate store            ─┘                             │
└─────────────────────────────────────────────────────────────────────┘
```

### 3.2 Komponenten und Verantwortlichkeiten

| Komponente | Verantwortung | Nicht-Verantwortung |
|---|---|---|
| `Cli` | Parsing, Validierung, Exit-Codes, Hilfe | Fachlogik |
| `Config` | YAML laden, validieren, Hash bilden, `m365` zurückschreiben | Secrets speichern |
| `Manifest` | Zustände, Runs, Ordner, Items, Events, Backup, Lock | Inhalte |
| `DavidSession` | Logon, Archiv-Enumeration, Metadaten, COM-Freigabe | Normalisierung |
| `DavidExporter` | Item → Staging (EML, MIME, Sidecar), Checksumme | EWS |
| `MimeSynth` | Header-Strip, synthetische Mails, Message-ID, Provenienz | Zustandsführung |
| `EwsAuth` | MSAL-Token, Zertifikat, Refresh, Token-Cache | Postfachlogik |
| `IOutputTarget` | Ausgabeport für Items: öffnen, schreiben, abschließen | EWS-/Graph-Typen |
| `LocalFileOutputTarget` | schreibt `items.jsonl` + EML (erste Implementierung) | Migration |
| `GraphOutputTarget` / `PstOutputTarget` | geplante Implementierungen, gleicher Port | Migration |
| `EwsOutputTarget` | MSAL-Zertifikat, Impersonation, Mail-Import via `CreateItems`/`SaveOnly`; implementiert `IOutputTarget` | Manifest |
| `EwsTargetMailbox` | spätere Erweiterung: Ordner, Kalender, Kontakte, ReadState, Retry | Manifest |
| `ContactMapper` | vCard-Test, direktes Feldmapping, Restfelder in den Body | EWS-Transport |
| `Pipeline` | Scan → Export → Import → Report, Backpressure, Abbruch | Formatdetails |
| `Reconciler` | `importing` auflösen, Dedupe per Message-ID, Nicht-Mail-Fallback | Import |
| `ReportWriter` | HTML/JSON/CSV, Zähler, Quarantäne | Migration |
| `Setup` | Graph/EXO-Orchestrierung, Cert, Allowlist, Verify, Remove | Postfächer |

### 3.3 Datenmodell (LiteDB)

LiteDB speichert Dokumente statt Tabellen. Das Modell bleibt fachlich gleich; die Collection-Namen und Indizes sind die neue „Schema-Definition".

```csharp
public sealed class ItemDoc
{
    [BsonId] public string ItemKey { get; set; } = ""; // mailbox|archive|davidId
    public string MailboxId { get; set; } = "";
    public string ArchiveScope { get; set; } = "";
    public string DavidId { get; set; } = "";
    public string SrcPath { get; set; } = "";
    public string ItemType { get; set; } = "";
    public string? MessageId { get; set; }          // Mail, 1:1
    public string? ReconcileKey { get; set; }       // nur Nicht-Mail
    public long SizeBytes { get; set; }
    public DateTime SrcReceivedUtc { get; set; }
    public bool? SrcRead { get; set; }
    public ItemState State { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public string? DstId { get; set; }
    public string? StagingFile { get; set; }
    public string? ContentSha256 { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime? ImportedUtc { get; set; }
    public DateTime? ReadStateUtc { get; set; }
}
```

```csharp
var db = new LiteDatabase(connectionString);
var items = db.GetCollection<ItemDoc>("items");
items.EnsureIndex(x => x.State);
items.EnsureIndex(x => x.MessageId);
items.EnsureIndex(x => x.MailboxId);
items.EnsureIndex(x => x.SrcReceivedUtc);
items.EnsureIndex(x => x.LastSeenUtc);

var folders = db.GetCollection<FolderDoc>("folders"); // eindeutig: MailboxId + SrcPath
var runs    = db.GetCollection<RunDoc>("runs");
var events  = db.GetCollection<EventDoc>("events");
var meta    = db.GetCollection<MetaDoc>("meta");      // schemaVersion, toolVersion
```

**Betriebsregeln:**

- **Ein Writer.** LiteDB ist thread-safe, aber parallele Schreibtransaktionen aus mehreren Mailbox-Workern werden über einen `ManifestWriter`-Channel serialisiert.
- **Checkpoint.** Nach jedem Lauf `db.Checkpoint()`; vor dem Backup zusätzlich `db.Dispose()` oder ein konsistenter Read-Only-Snapshot.
- **Backup.** `File.Copy` der `.db` nach Checkpoint, 7 Tage Rotation, Restore-Test; zusätzlich `--backup` als Kommando.
- **Repair.** Bei Verdacht auf Korruption `--repair` mit `Rebuild`; wenn das scheitert, aus Backup wiederherstellen und `importing` per Message-ID/ReconcileKey auflösen.
- **Nachweis.** `report --export-manifest` erzeugt CSV + JSON mit SHA-256. Das ist das langfristig lesbare Artefakt; die LiteDB ist der Arbeitszustand.
- **Schema-Version.** `meta.schemaVersion`; Migrationen werden beim Öffnen angewendet, nie stillschweigend.

**Primärschlüssel:** `ItemKey = {mailbox_id}|{archive_scope}|{david_id}`. Das ist stabil gegen Moves innerhalb des Archivs und eindeutig über Archive hinweg.

### 3.4 Zustandsautomat (präzisiert)

```
                 ┌─────────────┐
                 │ discovered  │◀──────────────────────────┐
                 └──────┬──────┘                           │
                        │ export                           │ keine Datei
                        ▼                                  │
                 ┌─────────────┐    Datei fehlt    ┌──────┴──────┐
                 │  exported   │──────────────────▶│  (reset)    │
                 └──────┬──────┘                   └─────────────┘
                        │ import (Batch)
                        ▼
                 ┌─────────────┐
                 │  importing  │
                 └──┬───────┬──┘
             ok     │       │ Fehler/Absturz
                    ▼       ▼
             ┌──────────┐  ┌──────────┐
             │ imported │  │  failed  │
             └──────────┘  └────┬─────┘
                                │ attempts < retries → discovered
                                │ attempts ≥ retries → skipped
                                ▼
                           ┌──────────┐
                           │ skipped  │◀── manuell / unknown-type / oversize
                           └──────────┘
```

Ergänzungen:

- **`importing`** wird für jedes Item geschrieben; der Reconcile läuft nach dem Absturz für alle Typen.
- **Reconcile-Reihenfolge:** Message-ID (Mail) → `ReconcileKey` (Nicht-Mail) → Fallback Betreff/Datum nur im Zielordner. Treffer setzt `imported` + `dst_id`.
- **`skipped`** ist ein Endzustand; `unskip` setzt zurück auf `discovered`; `skip`/`unskip` werden in `events` protokolliert.
- **`failed`** bleibt sichtbar; der Report zählt es getrennt von `skipped`.
- **Dry-Run** ändert keinen Item-Zustand; er schreibt nur den Run und die Statistik.

### 3.5 Schnittstellen (Ports)

```csharp
public interface IDavidSource
{
    IAsyncEnumerable<SourceItem> EnumerateAsync(MailboxMapping mapping, CancellationToken ct);
    Task<StagedItem> ExportAsync(SourceItem item, CancellationToken ct);
}

public interface IOutputTarget : IAsyncDisposable
{
    string Name { get; }
    Task OpenAsync(OutputContext context, CancellationToken ct);
    Task WriteItemAsync(OutputItem item, CancellationToken ct);
    Task CompleteAsync(OutputSummary summary, CancellationToken ct);
}

public interface IManifest
{
    Task<Run> BeginRunAsync(RunArgs args, CancellationToken ct);
    Task<ItemState> GetAsync(string itemKey, CancellationToken ct);
    Task SetStateAsync(string itemKey, ItemState state, string? error, CancellationToken ct);
    Task<IReadOnlyList<ItemRecord>> QueryAsync(ItemQuery query, CancellationToken ct);
}
```

Damit sind Unit-Tests ohne David und ohne Tenant möglich. Die Ausgabeseite ist bewusst ein Port: `LocalFileOutputTarget` ist die erste Implementierung, `GraphOutputTarget`, `PstOutputTarget` und `EwsTargetMailbox` können ohne Änderung der Pipeline nachgerüstet werden. Der Port enthält keine EWS-, Graph- oder PST-Typen.

### 3.6 Technologie-Stack (gepinnt)

| Baustein | Version | Begründung |
|---|---|---|
| .NET | 10.0 (LTS), `net10.0-windows`, x86 | DvApi32 ist 32-Bit; LTS-Support |
| EWS Managed API | `Microsoft.Exchange.WebServices.NETStandard` **1.1.3** | letzte stabile Version; 2.0.0-beta3 ist Pre-release; Supply-Chain-Risiko dokumentieren |
| MSAL | `Microsoft.Identity.Client` 4.92.0, gepinnt | Zertifikat-Token, erprobte Bibliothek |
| MIME | `MimeKit` (geplant) | Header-Manipulation; aktuell EML-Rohdaten |
| Manifest | `LiteDB` 5.0.21 | Dokumentmodell, Checkpoint, einfacher Code |
| YAML | `YamlDotNet` 18.1.0 | Konfiguration |
| Logging | Console + Datei (Serilog geplant) | strukturierte Logs |
| CLI | hand-rolled Parser (System.CommandLine geplant) | keine Beta-Abhängigkeit |
| Tests | xUnit/Verify (geplant) | Unit-/Snapshot-Tests |
| Paketverwaltung | `Directory.Packages.props`, `Directory.Build.props`, `NuGet.config` | zentrale Versionen, reproduzierbar |
| Lizenz | MIT (`SPDX-License-Identifier: MIT`) | OSS-Veröffentlichung |

**Supply-Chain-Auflage:** Das EWS-Paket ist ein Drittanbieter-Port ohne Microsoft-Support. Es wird exakt gepinnt, die Quelle archiviert, ein SCA-Scan in die Pipeline aufgenommen und ein minimaler SOAP-Fallback (nur die genutzten Operationen) als Plan B bewertet. Kein Floating. Für OSS: `THIRD-PARTY-NOTICES.md` und SBOM.

### 3.7 Projektstruktur

```
david2exchange/
  David2Exchange.slnx
  Directory.Build.props
  Directory.Packages.props
  NuGet.config
  LICENSE                         MIT
  README.md                       Englisch, öffentlich
  CONTRIBUTING.md
  SECURITY.md
  CODE_OF_CONDUCT.md
  CHANGELOG.md
  THIRD-PARTY-NOTICES.md
  .github/workflows/dotnet-release.yml
  src/David2Exchange/
    Program.cs
    Cli/                          Kommandos, Parser, Exit-Codes
    Config/                       MigrationConfig, Validator, ConfigHash
    Domain/                       Item, Folder, ItemState, Run, SkipReason
    Manifest/                     LiteDbManifest, Backup, Repair, Lock
    Source/                       DavidSession, DavidExporter, DavidTypes
    Normalize/                    MimeSynth, Provenance, MessageId
    Output/                       IOutputTarget, OutputTargetFactory, LocalFileOutputTarget
    Target/                       EwsTargetMailbox (implementiert IOutputTarget), EwsAuth, EwsRetry, ContactMapper
    Pipeline/                     DiscoverRunner, MigrationRunner, Reconciler
    Reporting/                    ReportWriter, CsvExporter, Counters
    Setup/                        TenantSetup, ProcessRunner, CertificateStore
    Setup/tenant-setup-graph.ps1
    Setup/tenant-setup-exo.ps1
    Setup/tenant-remove.ps1
    Infrastructure/               Clock, Paths, Hashing, Progress
  tests/
    David2Exchange.UnitTests/
    David2Exchange.IntegrationTests/   (opt-in, Test-Tenant)
  config/migration.example.yaml
  docs/SPIKE.md, docs/ADR/, docs/RUNBOOK.md, docs/ABNAHME.md, docs/CONTRACTS.md
```

### 3.8 EWS-only und End-of-Life

**Entscheidung:** Kein Graph-Adapter. EWS ist der einzige Target-Weg, weil die benötigte Funktionsparität für MIME-1:1, Nicht-Mail-Typen und Ordner-/Item-Semantik fehlt und weil der Tenant EWS ohnehin für Veeam und andere Werkzeuge betreibt.

**Konsequenzen:**

- Das Tool ist ein **Migrationswerkzeug auf Zeit**, kein Dauerprodukt. Es muss vor dem 01.04.2027 fertig sein; danach ist Exchange Online für EWS-endgültig zu.
- OSS-Nutzer brauchen denselben Hinweis; die README nennt EOL-Datum und Voraussetzungen (Allowlist, `EwsEnabled`, app-only).
- Exchange on-prem ist **nicht** Ziel; dort wäre die Authentifizierung anders. Eine spätere Erweiterung ist möglich, aber nicht Teil dieses Plans.
- Wenn G0 rot ist, wird nicht auf Graph ausgewichen, sondern eskaliert: Tenant-Admin, Microsoft-Support, ggf. Verschiebung des Cutovers. Die Architektur bleibt EWS.

### 3.9 Open Source und Mehrserverfähigkeit

**Lizenz und Repo:** MIT, Copyright 2026 BAUER GROUP. Jede Quelldatei trägt `SPDX-License-Identifier: MIT`. Öffentliche Doku Englisch; CLI-/Log-/Report-Texte Deutsch. Keine Interna im Repo (F-29). Die OSS-Veröffentlichung ist ein Nebeneffekt der sauberen Trennung, kein zweites Produkt: Sie kostet rund 10 h (P15), nicht 10 Wochen. Dieses Planungsdokument ist intern und nicht Bestandteil des Public Repos.

**Konfiguration statt Code:**

```yaml
david:
  server: david.example.local
  archiveRoot: \\david.example.local\david\archive
  progId: DVOBJAPILib.DvISEAPI      # je nach Installation anpassbar
  timezone: Europe/Berlin
  logon: AUTH

m365:
  tenantId: ""
  clientId: ""
  certThumbprint: ""
  ewsUrl: https://outlook.office365.com/EWS/Exchange.asmx
  provenanceDomain: david.example.local

staging:
  root: C:\david2exchange\staging
  manifest: C:\david2exchange\manifest.db
  logs: C:\david2exchange\logs

mailboxes:
  - id: user01
    source: { kind: user, archivePath: "user\\@me" }   # @me = Account.User.ID als Hex
    target: { upn: user01@example.com }
    include: [mail, fax, sms, chat, voice, calendar, contacts]
```

**Mehrserver-Regeln:**

- `david.server`, `archiveRoot`, `progId`, `timezone` sind Pflicht und werden von `doctor` geprüft.
- `source.kind` unterscheidet `user` und `common`; `archivePath` ist relativ und frei konfigurierbar.
- `discover --list-archives` listet die gefundenen Archive, damit Nutzer den richtigen Pfad wählen.
- `folderMap` und `excludeArchives` sind global und pro Postfach überschreibbar.
- Die Typelib-GUID wird **nicht** hartkodiert; spätes Binden über ProgID, optional Interop-Assembly.
- Testmatrix: David 12.x (eigene Installation) plus mindestens eine zweite Umgebung/Fixture; Abweichungen werden in `docs/COMPATIBILITY.md` dokumentiert.

**Fremdnutzer-Schnellstart:**

```text
1. Release-ZIP entpacken (Windows x64, .NET-Runtime im Paket).
2. config/migration.example.yaml kopieren und David-Server, ProgID,
   Archivpfade, Ziel-UPNs und Provenienz-Domain eintragen.
3. david2exchange doctor      # Voraussetzungen, Typelib, Netz, Zertifikat
4. david2exchange discover --list-archives
5. david2exchange discover --config migration.yaml
6. david2exchange run --config migration.yaml --dry-run
7. david2exchange run --config migration.yaml --mailbox <test> --limit 10 --newest
```

**Öffentliche Artefakte:** README, `docs/` (EN), `CONTRIBUTING`, `SECURITY`, `CODE_OF_CONDUCT`, `CHANGELOG`, Issue-/PR-Templates, Release-ZIP (self-contained x86), SHA-256, SBOM. Kein interner Outline-Link, keine Tenant-IDs, keine echten Postfachnamen.

### 3.10 Ausgabeport: lokal, Graph, PST, EWS

Die Ausgabeseite ist ein eigener Port, nicht der EWS-Adapter:

```text
MigrationPipeline ──► IOutputTarget
                         ├── LocalFileOutputTarget   (aktiv: JSONL + EML)
                         ├── GraphOutputTarget       (geplant)
                         ├── PstOutputTarget         (geplant)
                         └── EwsTargetMailbox        (geplant, implementiert denselben Port)
```

**Lebenszyklus:** `OpenAsync` → `WriteItemAsync` je Item → `CompleteAsync`. Der Port bekommt `OutputItem` (Metadaten, typisierte Felder, optionaler Staging-EML-Pfad) und enthält **keine** EWS-, Graph- oder PST-Typen. Damit ist ein neuer Ausgabeweg nur eine neue Klasse plus ein Eintrag im `OutputTargetFactory` – die Pipeline bleibt unverändert.

| Ausgabe | Status | Technik |
|---|---|---|
| `local` | aktiv | `items.jsonl`, `eml/`, `output-summary.json` |
| `graph` | geplant | Graph Mail/Import-Session; keine EWS-Abhängigkeit |
| `pst` | geplant | PST-Writer, ein PST je Postfach |
| `ews` | **aktiv (Mail-Import)** | `EwsOutputTarget` mit MSAL-Zertifikat, Impersonation, `CreateItems`/`SaveOnly`; Ordner-/Kalender-/Kontakt-Mapping folgt |

Der Staging-Pfad (`StagedEmlPath`) ist bewusst **nicht** im JSONL sichtbar; er dient nur dem Transfer zwischen David-Export und Ausgabeziel. `--output local|graph|pst` wählt den Port; nicht implementierte Werte enden mit Exit-Code 3 und einer klaren Erweiterungsanweisung.

---

## 4. Vorgehensmodell

### 4.1 Phasen und Gates

| Phase | Inhalt | Ergebnis | Gate |
|---|---|---|---|
| **0. Rettungsanker** (KW 45, 3 Tage) | Tenant-EWS-Status, Rollen, Test-App, Canary, D-10, Verträge/OSS-Skeleton | Go/No-Go EWS, eingefrorene Verträge | **G0** |
| **1. Spike & Design** (KW 46) | S0–S5, Message-ID-Test, LiteDB-Prototyp, ADRs | `SPIKE.md`, `CONTRACTS.md`, ADR-Set | **G1** |
| **2. Parallel-Build** (KW 46) | Streams A–D: Setup/OSS, Manifest/CLI, David/Export, EWS/Target | lauffähige Teilmodule, tägliche Integration | – |
| **3. Integration** (KW 47) | Kalender/Kontakte, Pipeline, Delta, Dedupe, Reconcile, Dry-Run | Ende-zu-Ende-Lauf | – |
| **4. Härtung & Test** (KW 47) | Fehlerinjektion, Performance, Security, Testautomatisierung, Doku | Testbericht, RC1 | – |
| **5. Interne Abnahme** (KW 47) | Abnahmekatalog, Fixes, Release Notes | RC2, Abnahmeprotokoll | – |
| **6. Pilot** (KW 48) | 3 Postfächer, 10/100/1000 Items, Outlook-Prüfung | Pilotabnahme | **G2** |
| **7. Rollout** (KW 49) | Initialläufe Postfach für Postfach, tägliche Deltas | alle Postfächer migriert | – |
| **8. Generalprobe** (KW 49–50) | Cutover-Probe im Test-Tenant, Rollback-Probe | Go/No-Go-Vorlage | – |
| **9. Cutover & Abbau** (KW 50–51, Reserve KW 02) | MX, ReadState, Finalreport, `setup --remove`, Hypercare | Migrationsnachweis, Tenant sauber | **G3/G4** |

### 4.2 Parallele Entwicklung mit Coding-Agenten

Ziel: Kalenderzeit verkürzen, ohne Integrationschaos. Der Gesamtaufwand bleibt ~144 h; die Parallelität hilft nur, wenn die Schnittstellen vorher stehen.

**Contract-first (KW 45, vor dem Branchen):**

- `docs/CONTRACTS.md` friert ein: `IDavidSource`, `IOutputTarget`, `IManifest`, DTOs, `ItemState`, Exit-Codes, Config-Schema, LiteDB-Dokumente, Fehlercodes.
- Shared Files (`Program.cs`, `Directory.Packages.props`, Interfaces, `MigrationConfig`) gehören dem Integrator. Agents ändern sie nur nach Abstimmung.
- Jeder Stream hat ein eigenes Git-Worktree und einen Branch; keine zwei Agents arbeiten an derselben Datei.

**Streams:**

| Stream | Pakete | Branch | Inhalt | Merge |
|---|---|---|---|---|
| A | P2, P15 | `feat/setup-oss` | Setup, Cert, Allowlist, OSS-Doku, CI-Release | nach B |
| B | P3, P4, P10 | `feat/core` | Config, CLI, LiteDB-Manifest, Report | zuerst |
| C | P5, P6 | `feat/david` | DvApi, Exporter, MimeSynth | nach B |
| D | P7, P8 | `feat/ews` | Auth, Ordner, Mail, Kalender, Kontakte | nach B |
| E | P9, P11, P12 | `integ` | Pipeline, Reconcile, Härtung, Tests | zuletzt |

**Regeln:**

- Merge-Reihenfolge: B → C/D (parallel) → A → E. `integ` wird täglich gebaut; `main` bleibt grün.
- Jeder Agent führt Unit-Tests und `dotnet build` aus; CI läuft pro Branch; nachts ein vollständiger Integrationslauf.
- Kein Agent merged nach `main`; der Integrator reviewt und merged. Menschliches Review bleibt Pflicht.
- Test-Tenant-Zugriffe werden serialisiert; EWS-Tests nicht parallel, um Throttling und falsche Fehler zu vermeiden.
- Merge-Konflikte werden vom Integrator aufgelöst, nicht vom Agenten „passend gemacht".
- Definition „merge-ready": grüne CI, Tests, keine offenen TODOs, Doku aktualisiert.

**Was Parallelität nicht löst:** Tenant-Abnahme, Outlook-Stichproben, Cutover und Fehlersuche bleiben seriell. Der kritische Pfad ist die Integration, nicht das Tippen.

### 4.3 Definition of Done (je Arbeitspaket)

1. Code kompiliert x86, keine Warnungen (bzw. begründete Suppressions).
2. Unit-Tests vorhanden und grün; kritische Pfade mit Negativtests.
3. Logs ohne Inhalte, mit `RunId`/`MailboxId`/`ItemId`.
4. Doku im Repo aktualisiert (README, ADR, Runbook, Contracts).
5. Review durchgeführt, Kommentare aufgelöst.
6. `dotnet publish` erzeugt lauffähiges x86-Artefakt.
7. Keine internen Identifier, `SPDX`-Header vorhanden.
8. Akzeptanzkriterium des Pakets im Testprotokoll nachgewiesen.

### 4.4 Dokumentationsartefakte

| Artefakt | Inhalt | Verantwortlich |
|---|---|---|
| `SPIKE.md` | S0–S5, Messwerte, Enum-/Property-Tabellen, Entscheidungen | Entwickler |
| `docs/CONTRACTS.md` | Interfaces, DTOs, Config-Schema, Fehlercodes | Integrator |
| `docs/ADR/` | Architekturentscheidungen D-01…D-13 | Entwickler/PL |
| `docs/RUNBOOK.md` | Betrieb, Delta, Cutover, Rollback, Eskalation | Betrieb |
| `docs/ABNAHME.md` | Testfälle, Ergebnisse, Unterschriften | PL/QA |
| `docs/COMPATIBILITY.md` | David-Versionen, Typelib, getestete Konfigurationen | Entwickler |
| `README.md` | Englisch, Schnellstart, Voraussetzungen, Build | Entwickler |
| `REPORT-<run>.html/json` | Migrationsnachweis je Lauf | Tool |

---

## 5. Zeitplan (Team-parallel)

Details zu Streams, Branches und Merge-Regeln: `docs/TEAM-PLAN.md`.

**Verbindliche Annahme:** 3 Entwickler + 4 Coding-Agenten + Integrator, Tenant-Admin und David-Admin mit je 10–20 % Verfügbarkeit. Die Einzelentwickler-Kurve (12–14 Wochen) ist Referenz, nicht Plan. Voraussetzung: G0 und Test-Tenant bis Ende KW 45, sonst verschiebt sich der Zielkorridor.

| KW | Zeitraum | Meilenstein / Inhalt | Gate |
|---|---|---|---|
| 45 | 02.–06.11.2026 | G0: Tenant-Status, Test-App, Canary; Skeleton + DvApi-Probe (**erledigt**) | **G0** |
| 46 | 09.–13.11.2026 | Streams A–D parallel: Typen/Export, Manifest/CLI, EWS/Setup, Tests/OSS; Spike S0–S5 | **G1** |
| 47 | 16.–20.11.2026 | Integration, Pipeline, Härtung, RC1, interne Abnahme | – |
| 48 | 23.–27.11.2026 | Pilot 3 Postfächer, Fixes | **G2** |
| 49 | 30.11.–04.12.2026 | Rollout Start, tägliche Deltas, Generalprobe | – |
| 50 | 07.–11.12.2026 | Cutover-Zielkorridor | **G3** |
| 51 | 14.–18.12.2026 | Hypercare / Rollback-Reserve | – |
| 52–53 | 21.12.2026–01.01.2027 | Feiertage / Puffer | – |
| 02–13 | 04.01.–31.03.2027 | Reserve, Nachmigration, Abbau | **G4** |

**Kritischer Pfad:** G0 → G1 → Integration → G2 → G3. Die Parallelität verkürzt die Build-Phase; Tenant-Freigabe, Pilot, Review und Cutover bleiben seriell.

**Puffer:** KW 51 plus KW 02–13; der Cutover liegt rund 3,5 Monate vor dem EWS-Ende. Wenn G1 oder G2 rutscht, wird der Cutover auf KW 02/2027 verschoben, nicht der Umfang gekürzt.

---

## 6. Arbeitspakete (WBS)

### 6.1 Übersicht

| ID | Paket | Ergebnis | h | Abhängig von |
|---|---|---|---|---|
| P0 | Phase 0 / Tenant-Gate | EWS-Status, Entscheidungen, Canary, Verträge | 4 | – |
| P1 | Spike S0–S5 | `SPIKE.md`, belegte Annahmen | 12 | P0 |
| P2 | Setup (Graph/EXO/Cert/Allowlist) | `setup`, `--check`, `--remove`, Backup/Restore | 10 | P1 |
| P3 | Foundation (Repo, CI, CLI, Config, Output-Port) | buildbare EXE, Config-Validierung, `IOutputTarget` | 6 | P1 |
| P4 | Manifest (LiteDB, Zustand, Lock, Backup) | `discover`/`run`-Skelett, Tests | 10 | P3 |
| P5 | David-Adapter (Logon, Scan, Export) | Items im Staging, Sidecars | 14 | P1 |
| P6 | Normalisierung (MimeSynth, Provenienz) | EML/MIME, Message-ID, Header-Strip | 8 | P5 |
| P7 | EWS-Kern (Auth, Ordner, Mail, Retry) | Import in Testpostfach | 14 | P1 |
| P8 | Kalender, Kontakte, ReadState | Termine/Kontakte im Ziel | 8 | P7 |
| P9 | Pipeline (Delta, Dedupe, Reconcile, Dry-Run, Limit) | Ende-zu-Ende-Lauf | 10 | P4, P6, P8 |
| P10 | Report, Logging, CSV | HTML/JSON/CSV, Zähler | 6 | P9 |
| P11 | Härtung & Security | Lock, Backup, PII, Fehlerpfade | 6 | P9 |
| P12 | Test & Abnahme | Automatisierte Tests, Protokoll | 12 | P11 |
| P13 | Pilot & Rollout-Support | Pilotabnahme, Betrieb | 8 | P12 |
| P14 | Cutover & Abbau | Runbook, Report, Decommission | 6 | P13 |
| P15 | OSS & Mehrserverfähigkeit | Lizenz, Public Docs, Config-Generalization, Release | 10 | P3 |
| | **Summe** | | **144** | |
| | Puffer 15 % | | **22** | |
| | **Gesamt Entwicklung** | | **166** | |
| | Betrieb (Discover, Mapping, Reports, Cutover, Hypercare) | | **34** | |
| | **Gesamt** | | **200 h ≈ 25 PT** | |

**Parallelisierung:** 4 Streams (A–D) plus Integrator, 3 Entwickler + 4 Agenten. Erwartete Kalenderzeit bis RC1 **2 Wochen**, Pilot KW 48, Cutover KW 50. Der Gesamtaufwand bleibt 200 h – Agenten sparen Kalenderzeit, nicht Abnahme- und Testzeit.

### 6.2 Paketdetails (kritische Pakete)

#### P0 – Phase 0 / Tenant-Gate (4 h)

- `Get-OrganizationConfig -RetrieveEwsOperationAccessPolicy` lesen, `EwsEnabled`/`EwsAllowedAppIDs` dokumentieren, Backup ziehen.
- Rollen und Module prüfen (`Connect-MgGraph`, `Connect-ExchangeOnline`, Versionen).
- Test-App-ID eintragen (mit Freigabe), Canary-`Folder.Bind` gegen Testpostfach.
- Verträge (`CONTRACTS.md`), OSS-Skeleton, Branch-/Worktree-Setup.
- **DoD:** unterschriebenes G0-Protokoll mit Status, Risiko und nächstem Schritt.

#### P1 – Spike (12 h)

| ID | Test | Abnahme |
|---|---|---|
| S0 | EWS Managed API 1.1.3 auf .NET 10 x86: `OAuthCredentials`, Token, `Folder.Bind`, `CreateItem` | funktioniert oder klarer Fallback |
| S1 | `CreateItem` mit `MimeContent` + `PidTagMessageFlags` + `PidTagMessageDeliveryTime` in Inbox **und** SentItems; Message-ID binden und vergleichen | empfangen, kein Entwurf, Datum korrekt, **Message-ID identisch** |
| S2 | DvApi: Fax, SMS, Voice, Chat exportieren; Enums/Properties; `_ID`-Stabilität; Unverteilt-Zugriff | Typ 1=Adresse/2=Mail belegt; restliche Typen + Formate dokumentiert |
| S3 | Kontakt: vCard testen; bei Nichtunterstützung Feldmapping | D-12: vCard nur bei vollständiger Feldübernahme; sonst Mapping-Beleg |
| S4 | Dedupe: `FindItems` auf `InternetMessageId`; Reconcile nach Kill; Nicht-Mail-Fallback | belastbarer Schlüssel, inkl. Kalender/Kontakt |
| S5 | Große Items/Speicher: 50/100/120 MB, 2 parallel; LiteDB-Checkpoint/Backup/Repair | Grenze und Verhalten dokumentiert |

#### P2 – Setup (10 h)

- Zwei `pwsh`-Prozesse (Graph, EXO), JSON-Vertrag, Timeout, Exit-Code-Mapping.
- Zertifikat erzeugen/wiederverwenden, Thumbprint, Ablauf, Renewal.
- App idempotent anlegen/aktualisieren (ObjectId-Marker), Consent, Access Policy.
- Allowlist: Backup, Read-Modify-Write, Verify, Restore bei Abweichung.
- `--check` (inkl. Ablaufwarnung, Allowlist-Status, Canary), `--remove`.
- **DoD:** zweimaliges `setup` ohne Duplikat; `--remove` stellt Ausgangszustand wieder her (außer `EwsEnabled`).

#### P5 – David-Adapter (14 h)

- COM-Logon auf STA-Thread; `IDavidApi`-Fassade; Freigabe der COM-Objekte.
- Archiv-Enumeration inkl. Unverteilt, Excludes, leere Ordner, Tiefe.
- Metadaten-Scan (ID, Typ, Datum, Größe, Betreff, Lesestatus) ohne Inhalte.
- Export je Typ, Temp-Datei → Staging, atomarer Move, SHA-256, Sidecar.
- Typ-Tabelle aus S2, unbekannte Typen → `skipped` + Report.
- `discover --list-archives`; ProgID konfigurierbar; `user\@me` über `Account.User.ID`.
- **DoD:** 1.000 Items exportiert, keine offenen COM-Handles, Abbruch sauber.

#### P7 – EWS-Kern (14 h)

- MSAL-Zertifikat-Token, Refresh < 5 min, `X-AnchorMailbox`, Impersonation.
- Ordner: Root/Prefix, Well-Known, Pfad, Sanitizing, Paging, Kollisionen, Cache.
- Mail: MIME, Flags, DeliveryTime, SubmitTime, Receipt-Flags, Batch 40/25 MB, Response-Mapping.
- Message-ID nach Import verifizieren; Dedupe per `FindItems`.
- Retry: `ServerBusy` → Backoff; 503/Timeout → exponentiell; 401/403 → Lauf stoppen, kein `failed`.
- Batch-Splitting bei Transportfehler bis Einzelitem.
- **DoD:** 5.000 Items ohne Abbruch, Throttling messbar, keine Duplikate, Message-ID 1:1.

#### P8 – Kalender, Kontakte, ReadState (8 h)

- Kalender: Einzeltermine, Serienexpansion, DST, `SendToNone`, keine Teilnehmer.
- Kontakte: S3 testet vCard. Nur wenn EWS alle Testfelder erhält, ist der vCard-Pfad erlaubt; sonst wird **direkt gemappt**.
- `ContactMapper`: `DisplayName`, `GivenName`, `Surname`, `CompanyName`, `Department`, `JobTitle`, `EmailAddress1..3`, `PhoneNumbers` (Business, Mobile, Home, BusinessFax), `PhysicalAddresses` (Business, Home), `Body` (Notizen), `FileAs`.
- Nicht abbildbare David-Felder werden als Text an den Body angehängt; Provenienz ebenfalls, da Kontakte keine Header haben.
- vCard-Ausfall darf nie zu `skipped` führen; höchstens zu einem dokumentierten Feldverlust.
- **DoD:** Kontakt mit allen abbildbaren Feldern inkl. Unicode/Notizen; vCard-Fehler → vollständiges Mapping; Termine Januar/Juli korrekt.
- **Hinweis:** Der Mapping-Aufwand ist in den 8 h enthalten; falls er deutlich größer wird, greift der Puffer.

#### P9 – Pipeline (10 h)

- Scan → Diff → Export → Import → ReadState → Report.
- `importing`-Reconcile per Message-ID; Nicht-Mail über `ReconcileKey` oder Fallback.
- `--dry-run` ohne Item-Zustandsänderung; `--limit` deterministisch (Tie-Breaker `david_id`).
- `skip`/`unskip`/`reset`; Lock; Ctrl+C schreibt Batch zu Ende.
- **DoD:** Kill-Test, Resume-Test, 0-Item-Zweitlauf.

---

## 7. Test- und Abnahmekonzept

### 7.1 Teststufen

| Stufe | Inhalt | Umgebung | Automatisierung |
|---|---|---|---|
| T1 Unit | Zustandsautomat, Sanitizing, Zähler, Config, MIME, Retry | lokal | ja, CI |
| T2 Component | Fake-David + Fake-EWS, Contract-Tests | lokal | ja, CI |
| T3 Integration | echter David-Testarchive + Test-Tenant | INT | teilautomatisiert |
| T4 E2E/Abnahme | Konzept Kap. 14 + T-01…T-22 | INT/PROD-Testpostfach | protokolliert |
| T5 Performance | 5.000/50.000 Items, Throttling, Speicher | INT | Messskript |
| T6 Failure Injection | Kill, Netz weg, 401, 503, Disk voll, LiteDB kaputt, Cert abgelaufen | INT | teilautomatisiert |
| T7 Security/Privacy | Logs, ACL, Policy, Secrets, CSV-Injection | INT | Review + Tests |
| T8 Datenqualität | Zähler, Stichproben, Hashes, S/MIME, DST, Message-ID | INT | Stichprobe |
| T9 OSS/Mehrserver | Clean-Clone, Beispielconfig, keine Interna, zweite David-Config | lokal/INT | CI + Review |

### 7.2 Zusätzliche Testfälle (über Kap. 14 hinaus)

| ID | Test | Erwartung |
|---|---|---|
| T-01 | EWS im Tenant deaktiviert | `run` bricht mit klarer Anleitung ab, kein Item `failed` |
| T-02 | Allowlist-Freigabe verzögert (24 h) | `setup --check` meldet „pending", kein falscher Fehler |
| T-03 | Zielquota zu klein | `discover` stoppt bzw. markiert Postfach rot, bevor importiert wird |
| T-04 | Datenträger voll beim Export | Lauf stoppt sauber, keine halben Dateien, Resume möglich |
| T-05 | LiteDB korrupt | Restore aus Backup, klare Meldung, kein Datenverlust |
| T-06 | Zwei `run` parallel | zweiter Lauf bricht mit Lock-Hinweis ab |
| T-07 | Unicode-/Long-Path-Ordner (300 Zeichen) | Import erfolgreich, Name gekürzt + Hash, Report |
| T-08 | S/MIME-signierte Mail | unverändert importiert, Hinweis im Report, keine Receipt |
| T-09 | Termin im Januar und Juli (DST) | korrekte Uhrzeit in Outlook |
| T-10 | Kontakt mit Foto/Notizen | alle abbildbaren Felder gesetzt, Rest im Body; Foto dokumentiert, falls EWS es nicht kann |
| T-11 | 0-Byte-Anhang, eingebettete Mail, TNEF | kein Absturz, Anhang erhalten oder dokumentiert |
| T-12 | 100 MB Item bei x86 | definiertes Verhalten (skip oder Erfolg), kein OOM |
| T-13 | CSV-Export mit Formelzeichen | Zellen escaped, keine Formelausführung |
| T-14 | Log-Review auf Inhalte | keine Betreff-/Body-Fragmente außer konfiguriert |
| T-15 | `setup --remove` | Allowlist unverändert (fremde IDs), `EwsEnabled` unverändert, App/Cert weg |
| T-16 | Message-ID nach Import | identisch zur Quelle (Klammern/Case/Whitespace) |
| T-17 | Kill während Kalender/Kontakt-Import | Reconcile ohne Duplikat (ReconcileKey oder Fallback) |
| T-18 | Clean-Clone-Build | `dotnet build`/`publish` ohne David-Client, Beispielconfig lädt |
| T-19 | Public-Repo-Scan | keine internen Hostnamen/Domains/UPNs/Pfade |
| T-20 | Zweite David-Konfiguration | `doctor` grün, `discover` findet Archive, keine Codeänderung |
| T-21 | vCard-Import nicht unterstützt | Kontakt wird per Feldmapping vollständig angelegt, kein `skipped`, Restfelder im Body |
| T-22 | Ausgabeport | `local` schreibt JSONL/EML; `--output graph`/`pst` endet mit Exit 3 und Erweiterungshinweis; keine Target-Typen im Port |

### 7.3 Abnahmeprozess

1. **Testbereitschaft:** RC1 gebaut, Testdaten bereit, Tenant freigegeben.
2. **Durchführung:** T4/T5/T6 mit Protokoll; jeder Testfall mit Datum, Ergebnis, Screenshot/Logauszug.
3. **Abweichungen:** klassifiziert (blockierend / nicht blockierend / Beobachtung); blockierende müssen behoben oder als Risiko akzeptiert werden.
4. **Abnahme:** PL, Betrieb, Tenant-Admin, ggf. Maintainer unterschreiben; Restrisiken werden ausgewiesen.
5. **Go/No-Go:** G2 (Pilot) und G3 (Cutover) mit expliziten Kriterien; kein „implizites Go".

---

## 8. Betrieb, Cutover und Rollback

### 8.1 Betriebsmodell

| Element | Festlegung |
|---|---|
| Run-User | dediziertes Konto mit David-Rechten, keine Alltags-Mailbox |
| Migrations-VM | dediziert, BitLocker, restricted ACL, definierter Autologon |
| Ausführung | `run --config ...`; Delta täglich, Unverteilt optional stündlich |
| Überwachung | Task-Scheduler-Status + täglicher `setup --check` + Report-Zähler |
| Alarmierung | fehlgeschlagener Lauf, 401/403, Quota, Zertifikat < 30 Tage |
| Aufbewahrung | LiteDB + CSV/JSON + Report + Setup-Backup; Logs 90 Tage |
| Zugriff | nur Migrationsteam + Audit; Staging nach Cutover löschen |

### 8.2 Cutover-Runbook (Auszug)

| Zeit | Schritt | Verantwortlich | Check |
|---|---|---|---|
| T-14 | Änderungsstopp, letzter Volldelta, Kapazitätscheck, Runbook-Review | PL/Ops | Zähler, Quota, offene Fehler |
| T-7 | Generalprobe im Test-Tenant, Restore-Test, Rollback-Probe | Dev/Ops | G3-Kriterien |
| T-1 | Finaler Delta, MX-TTL senken, Kommunikation, Bereitschaft | Ops | Queue leer, Report grün |
| T0 | MX auf EXO, David-POP3/Verteilung aus, 60 min warten | Admin | Mailfluss |
| T0+1h | `run --readstate-days 30`, finaler Delta | Ops | 0 neue Items |
| T0+2h | `report --export-manifest`, Smoke-Tests (Mail, Fax, Termin, Kontakt) | PL/Key User | Stichprobe |
| T0+4h | Go/No-Go Rollback entscheiden | PL | Kriterien |
| T+1 | Hypercare, Monitoring, Nachmigration | Ops | offene Fehler |
| T+5 | David schreibgeschützt, Abnahme | PL | Report |
| T+14 | `setup --remove`, Staging löschen, Archivierung | Admin | G4 |

### 8.3 Rollback

- **Auslöser:** kritischer Datenverlust, Massenfehler, Mailfluss gestört, Quota.
- **Maßnahme:** MX zurück auf David, David-POP3/Verteilung wieder aktivieren, Import stoppen. Bereits importierte Items bleiben; sie werden beim nächsten Lauf über die Message-ID erkannt.
- **Grenze:** Ein Rollback nach T0 ist ein Betriebsereignis, kein Code-Rollback. Deshalb ist die Generalprobe Pflicht.
- **Nachbereitung:** Root Cause, Report, Entscheidung über zweiten Cutover-Versuch.

### 8.4 Decommissioning

1. `setup --remove` (App, Consent, Cert, Allowlist-Eintrag; `EwsEnabled` unverändert).
2. Allowlist gegen Backup prüfen; fremde IDs unverändert.
3. Staging und Logs nach Retention löschen; Löschprotokoll.
4. LiteDB, CSV/JSON, Report, Setup-Backup archivieren (10 Jahre oder nach Policy).
5. Repo archivieren, README mit Datum und Ergebnissen.

---

## 9. Governance, Sicherheit und Datenschutz

### 9.1 Rollen und RACI

| Aktivität | PL | Dev | Tenant-Admin | David-Admin | Betrieb | Key User |
|---|---|---|---|---|---|---|
| G0–G4 | A | R | C | C | C | I |
| Setup/Tenant | A | C | R | I | I | I |
| Migration Run | A | C | I | C | R | I |
| Skip-Entscheidung | A | I | I | C | R | C |
| Cutover | A | C | R | R | R | C |
| Decommission | A | I | R | C | C | I |

A = accountable, R = responsible, C = consulted, I = informed.

### 9.2 Datenschutz ist Betreiberpflicht

Das Projekt baut keinen Freigabeprozess. Der Betreiber bleibt für die rechtlichen Voraussetzungen der Migration verantwortlich; das Tool liefert die technischen Bausteine:

- **Zugriff:** Application Access Policy, nur Zielpostfächer, protokollierte Admins.
- **Speicher:** Staging und LiteDB auf verschlüsseltem Volume, ACL nur Migrationsteam.
- **Logs:** keine Inhalte, PII-Toggle im Report, MSAL-PII-Logging aus.
- **Löschung:** Staging/Logs nach Cutover + Retention löschen; Nachweis behalten.
- **Transparenz:** Betreiber informiert Mitarbeiter; das ist keine Projektaufgabe.

### 9.3 Sicherheit

| Thema | Maßnahme |
|---|---|
| Least Privilege | Application Access Policy verpflichtend; nur Zielpostfächer |
| Credentials | Zertifikat in `CurrentUser\My`, private Key nicht exportieren, ACL prüfen |
| Secrets | keine Secrets in YAML/Repo; `.gitignore` für `migration.yaml`, `*.pfx`, `manifest.db`, `staging/`, `logs/` |
| Transport | TLS 1.2+, Proxy-konform, keine Zertifikatsprüfung deaktivieren |
| Logging | keine Inhalte, PII-Toggle, MSAL-PII-Logging aus |
| Supply Chain | Pakete pinnen, Lockfile, SCA-Scan, Quellcode archivieren, Code-Signing |
| Angriffsfläche | keine Netzwerkdienste, keine GUI, nur ausgehend HTTPS |
| Audit | Setup-/Run-/Skip-/Remove-Ereignisse mit Zeit, User, Host |
| OSS | MIT, Third-Party-Notices, SBOM, Security-Policy, keine Interna |

### 9.4 Aufbewahrung und Nachweis

Der Nachweis besteht aus: LiteDB (Arbeitszustand), `manifest.csv`, `manifest.json`, `report.html`, `report.json`, `setup-backup-*.json`, Logs, Tool-Version/Commit, Config-Hash, SHA-256-Summen. Ablage in einem revisionssicheren Verzeichnis; Zugriff nur Audit/PL. Der Report enthält eine Signaturzeile (Wer, Wann, Was) und die Zählerformel.

### 9.5 Open-Source-Governance

- **Maintainer:** BAUER GROUP; Contributions über PRs, Review durch Maintainer.
- **Lizenz:** MIT; keine zusätzlichen Einschränkungen; `SECURITY.md` für Meldungen.
- **Versionierung:** SemVer; `CHANGELOG.md`; Releases mit ZIP, SHA-256, SBOM.
- **EOL:** README und Release Notes nennen den 01.04.2027; das Tool ist ein Migrationswerkzeug, kein Dauerprodukt.
- **Keine Interna:** Public-Repo-Check in CI (Hostnamen, Domains, UPNs, Pfade, Tenant-IDs).

---

## 10. Risikoregister

W = Wahrscheinlichkeit (1–5), A = Auswirkung (1–5), S = W × A.

| ID | Risiko | W | A | S | Frühwarnindikator | Maßnahme | Owner |
|---|---|---|---|---|---|---|---|
| R-01 | EWS-Allowlist/Freigabe scheitert oder EWS endet früher | 3 | 5 | 15 | Canary 403, Liste nicht änderbar | G0, Tenant-Admin, Cutover KW 50, täglicher Check | PL |
| R-02 | Message-ID wird nicht 1:1 gespeichert oder ist nicht filterbar | 3 | 5 | 15 | S1/S4 fehlgeschlagen | S1/S4 vor Bau, Fallback 0x1035, ggf. Reconcile-Key | Dev |
| R-03 | DvApi-Export für Fax/SMS/Voice/Chat nicht wie erwartet | 4 | 4 | 16 | S2 unvollständig | Puffer, Alternative je Typ, Report | Dev |
| R-04 | Duplikate durch Reconcile-Lücke (Nicht-Mails) | 3 | 4 | 12 | Kill-Test findet Duplikat | ReconcileKey, Test T-17, Zählung | Dev |
| R-05 | Quota/Postfachgröße reicht nicht | 3 | 4 | 12 | `discover` > 80 % Ziel | Kapazitätscheck, Lizenz/Archiv, Split | Admin |
| R-06 | Datenverlust/Beschädigung im Staging | 2 | 5 | 10 | Hash-Mismatch, 0-Byte-Datei | SHA-256, Re-Export, Backup | Dev |
| R-07 | LiteDB korrupt / paralleler Lauf | 2 | 4 | 8 | Lock-Fehler, DB-Fehler | Checkpoint, Backup, Lock, Repair-Test | Dev |
| R-08 | Zeitplan rutscht (Krankheit, DvApi-Überraschung) | 3 | 3 | 9 | Meilenstein > 1 Woche | Puffer, Scope-Priorisierung, Gate | PL |
| R-09 | PII in Staging/Logs/Report | 2 | 4 | 8 | Log-Review findet Inhalt | ACL, Verschlüsselung, PII-Toggle, Cleanup | Dev |
| R-10 | 32-Bit-Speicher/OOM bei großen Items | 3 | 3 | 9 | OOM im Test | Limit 50 MB, seriell, Messung | Dev |
| R-11 | DvApi-Sitzung im Task unzuverlässig | 3 | 3 | 9 | Delta fehlt | dedizierte VM, Monitoring, manueller Fallback | Ops |
| R-12 | Zertifikat läuft ab | 2 | 3 | 6 | < 30 Tage | `--check`, Renewal, Kalender | Admin |
| R-13 | EWS-Paket/Supply Chain | 2 | 4 | 8 | CVE, Buildfehler | Pin, SCA, Vendor, SOAP-Fallback | Dev |
| R-14 | Netzwerk/Proxy/TLS | 2 | 3 | 6 | Timeout | `doctor`, Proxy-Config, Test | Ops |
| R-15 | Abnahme verzögert durch fehlende Testdaten | 3 | 3 | 9 | Testpostfach fehlt | Testdaten früh, Tenant reservieren | PL |
| R-16 | Key-Person-Abhängigkeit | 3 | 3 | 9 | nur eine Person kann Tool bedienen | Doku, Runbook, Übergabe, Review | PL |
| R-17 | Parallele Agenten: Merge-Konflikte/inkonsistente Verträge | 3 | 3 | 9 | tägliche Integration rot | Contract-first, Integrator, kleine PRs | Integrator |
| R-18 | Mehrserverfähigkeit scheitert an Typelib/Archivlayout | 3 | 3 | 9 | `doctor` bei zweiter Config rot | spätes Binden, ProgID, Testmatrix | Dev |
| R-19 | OSS-Veröffentlichung leakt Interna | 2 | 4 | 8 | Public-Repo-Scan findet Host/Domain | Scrubbing, CI-Check, Review | PL |

**Top-3:** R-01, R-03, R-02. Sie entscheiden über Termin und Machbarkeit, nicht über Codequalität. Deshalb liegen G0 und G1 vor dem Bau.

---

## 11. Aufwand, Kosten und Make-or-Buy

### 11.1 Aufwandsszenarien

| Szenario | Aufwand | Dauer | Risiko | Kommentar |
|---|---|---|---|---|
| „Konzept 37 h" | 37 h + 8 h Puffer | 5–7 Tage | hoch | Prototyp; keine Härtung/Testautomatisierung/OSS/Betrieb |
| **Professional, Team-parallel (Empfehlung)** | **144 h + 22 h Puffer + 34 h Betrieb** | **2 Wochen bis RC1; Cutover KW 50** | mittel | auditierbar, wiederaufnehmbar, OSS-fähig, getestet |
| Zusätzliche David-Testumgebung | +16–24 h | +1 Woche | niedrig | nur falls eine zweite Installation verfügbar ist |

**Begründung der Differenz:** Die 37 h decken das „Happy Path"-Coding. Nicht enthalten sind: Setup-Idempotenz und Allowlist-Restore, LiteDB-Backup/Repair, Reconcile für Nicht-Mails, Fehlerinjektion, Performance-/Throttling-Messung, Testautomatisierung, OSS-Doku/Release, Runbook, Pilot- und Cutover-Support. Die parallele Entwicklung senkt die Kalenderzeit, nicht den Aufwand.

### 11.2 Make-or-Buy

| Option | Vorteil | Nachteil | Bewertung |
|---|---|---|---|
| Eigenentwicklung | exakt die geforderten Typen/Provenienz/Manifest; OSS | 144 h, Wartung, DvApi-Risiko | **empfohlen**, wenn Anforderungen stabil |
| t2ex Single Server Pro (499 €) | schnell, erprobt | kein Delta-Manifest, keine Fax/SMS/Voice-Synthese, kein Setup | nur als Mail-Baseline denkbar |
| Kaufwerkzeug + Eigenlogik | weniger Code | zwei Systeme, Schnittstellen, Lizenz | prüfen, wenn Termin kritischer als Aufwand |

### 11.3 Kosten

Die Kosten sind intern: Aufwand × Vollkostensatz. Es fallen keine Lizenzkosten für das Tool an; externe Kosten entstehen nur bei Kaufoption, Zertifikaten (interne PKI) und Betrieb. Die 499 € des Vergleichsprodukts sind kein Argument gegen die Eigenentwicklung, weil die geforderten Nachweis- und Nicht-Mail-Funktionen dort fehlen – aber sie sind ein Argument für eine Make-or-Buy-Prüfung, falls der Umfang reduziert wird.

---

## 12. Kommunikation und Stakeholder

| Stakeholder | Interesse | Format | Frequenz |
|---|---|---|---|
| Auftraggeber/IT-Leitung | Termin, Risiko, Budget | Statusblatt + Gate-Entscheid | wöchentlich |
| Tenant-Admin | Setup, Allowlist, Consent | technische Abstimmung | nach Bedarf |
| David-Admin | Archive, Rechte, Sitzung | Abstimmung + Testzugang | nach Bedarf |
| Key User | Ergebnis, Stichproben | Testmailbox + Abnahme | Pilot/Cutover |
| Mitarbeiter | Was ändert sich, was nicht | Info-Mail vor Cutover | T-14/T-1 |
| Betrieb | Runbook, Monitoring, Eskalation | Übergabe + Shadowing | vor Rollout |
| OSS-Maintainer/Community | Lizenz, Issues, Releases | GitHub, Changelog | nach Veröffentlichung |

**Eskalationsweg:** Dev → PL → IT-Leitung; bei EWS-Blockade sofort PL + Tenant-Admin + Microsoft-Support.

---

## 13. Sofortmaßnahmen (nächste 10 Arbeitstage)

**Tag 1 – Tenant-Realität**

```powershell
Connect-ExchangeOnline
$cfg = Get-OrganizationConfig -RetrieveEwsOperationAccessPolicy
$cfg | Format-List EwsEnabled, EwsAllowedAppIDs
$cfg.EwsAllowedAppIDs | Set-Content "C:\temp\ews-allowlist-backup-$(Get-Date -f yyyyMMdd).txt"
Get-Module -ListAvailable ExchangeOnlineManagement, Microsoft.Graph.Applications | Select Name, Version
```

Ergebnis dokumentieren: Ist EWS aktiv? Ist die Liste änderbar? Welche fremden IDs sind enthalten? Wurde der Tenant am 1.10. automatisch gesperrt?

**Tag 2 – Entscheidungen und Verträge**

- D-10 (`EwsEnabled`), D-09 (Access Policy), D-06 (PK) vorbereiten; Test-Tenant/Testpostfach reservieren.
- `docs/CONTRACTS.md` anlegen: Interfaces, DTOs, Config-Schema, Exit-Codes.
- Repo-Skeleton für OSS: LICENSE, README (EN), CONTRIBUTING, SECURITY, CI, `.gitignore`.
- Test-App-ID eintragen (falls freigegeben), Canary `Folder.Bind` bauen.

**Tag 3–5 – Spike S0/S1/S4 (parallel)**

- EWS Managed API + MSAL + Zertifikat auf .NET 10 x86.
- MIME-Import mit Flags/DeliveryTime in Inbox und SentItems; Message-ID 1:1 prüfen.
- Dedupe über `InternetMessageId`; Kill/Reconcile-Simulation; Nicht-Mail-Fallback.
- LiteDB-Prototyp: Checkpoint, Backup, Restore.

**Tag 6–7 – Spike S2/S3/S5 (parallel)**

- DvApi-Export je Typ, Enums, `_ID`-Stabilität, Unverteilt.
- Kontakt-Weg (vCard testen, sonst Feldmapping), Speichergrenze, 2-Postfach-Parallelität.
- Zweite David-Konfiguration/`progId` testen, falls verfügbar.

**Tag 8 – Gate G1**

- `SPIKE.md`, `CONTRACTS.md`, ADRs, korrigierte Schätzung, Go/No-Go.
- Bei Rot: Scope anpassen, nicht die Architektur.

**Tag 9–10 – Streams starten**

- Branches/Worktrees A–D anlegen; Integrator benennen.
- Manifest, CLI, Config, David-Adapter, EWS-Adapter parallel beginnen.
- Tägliche Integration ab Tag 10.

---

## 14. Offene Punkte und Freigaben

| ID | Punkt | Owner | Fällig | Status |
|---|---|---|---|---|
| O-01 | Tenant-EWS-Status und Freigabe einer neuen App-ID | Tenant-Admin | 04.11.2026 | offen |
| O-02 | LiteDB-Schema, Backup-/Export-Pfad | PL/Dev | 06.11.2026 | offen |
| O-03 | Freigabe `EwsEnabled=true` (Sicherheit) | IT-Leitung | 04.11.2026 | offen |
| O-04 | Zielpostfächer, Quota, Shared Mailboxes | Admin | 11.11.2026 | offen |
| O-05 | OSS-Repo, Lizenz, Public-Doku, CI | PL/Dev | 11.11.2026 | offen |
| O-06 | Cutover-Fenster KW 50/2026 | PL | 20.11.2026 | offen |
| O-07 | Test-Tenant/Testpostfächer | Admin | 06.11.2026 | offen |
| O-08 | Run-User/DvApi-Sitzung | Ops | 20.11.2026 | offen |
| O-09 | Zertifikats-/PKI-Prozess | Admin | 20.11.2026 | offen |
| O-10 | Umfang der Nicht-Mail-Typen (Chat/Voice) | Fachbereich | 11.11.2026 | offen |
| O-11 | Zweite David-Testumgebung/Fixture | David-Admin | 20.11.2026 | offen |

---

## 15. Anhänge

### A. Setup-Review-Checkliste

- [ ] `pwsh` ≥ 7.4, Module in kompatibler Version, PSGallery erreichbar.
- [ ] Graph-Login erfolgreich, Rollen ausreichend.
- [ ] EXO-Login erfolgreich, `Set-OrganizationConfig`-Parameter vorhanden.
- [ ] Zertifikat vorhanden, privater Schlüssel lesbar, Ablauf > 6 Monate.
- [ ] App eindeutig (ObjectId-Marker), `full_access_as_app`, Consent erteilt.
- [ ] Access Policy gesetzt und für alle Ziel-UPNs getestet.
- [ ] Allowlist-Backup vorhanden, Diff dokumentiert, fremde IDs unverändert.
- [ ] `EwsEnabled` bewusst gesetzt, Freigabe dokumentiert.
- [ ] Canary-`Folder.Bind` erfolgreich (oder „pending" mit Frist).
- [ ] `migration.yaml` aktualisiert, keine Secrets im Repo.
- [ ] `--check` grün, `--remove`-Probe im Test-Tenant erfolgreich.

### B. Runbook-Vorlage (Auszug)

1. Vorab: `setup --check`, freier Platz, LiteDB-Backup, letzter Report.
2. Start: `run --config ... --mailbox <id>`; Fortschritt beobachten.
3. Störung: Lauf stoppen, Log sichern, Ursache klassifizieren (Umgebung/Item), nicht blind wiederholen.
4. Abschluss: Report prüfen (`Quelle = importiert + failed + skipped + ausgeschlossen`), Stichprobe, Run schließen.
5. Eskalation: 401/403 → Tenant; Quota → Admin; DvApi → David-Admin; OOM → Item-ID + Größe.

### C. Abnahmeprotokoll (Felder)

Run-ID, Datum, Tool-Version/Commit, Config-Hash, Tester, Testfälle mit Ergebnis, Abweichungen, Restrisiken, Unterschriften PL/Betrieb/Tenant-Admin.

### D. ADR-Vorlage

Titel, Status, Kontext, Entscheidung, Alternativen, Konsequenzen, Datum, Owner, Bezug zu Befund/Entscheidung.

### E. OSS-Release-Checkliste

- [ ] `LICENSE` (MIT) vorhanden, `SPDX`-Header in allen Quelldateien.
- [ ] README (EN) mit Voraussetzungen, Schnellstart, EWS-EOL-Hinweis.
- [ ] `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`, `CHANGELOG.md`.
- [ ] `THIRD-PARTY-NOTICES.md`, SBOM, Pakete gepinnt, Lockfile.
- [ ] Beispielconfig ohne interne Hostnamen/Domains/UPNs/Pfade.
- [ ] CI: Build x86, Tests, Public-Repo-Scan, Release-ZIP + SHA-256.
- [ ] Keine Tenant-IDs, Zertifikate, Logs, Manifeste im Repo.
- [ ] EOL-Datum und Support-Grenze dokumentiert.

### F. Quellen und Referenzen

- Microsoft Learn: *Deprecation of Exchange Web Services in Exchange Online* – https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/deprecation-of-ews-exchange-online
- Microsoft Tech Community: *Exchange Online EWS, Your Time is Almost Up* – https://techcommunity.microsoft.com/blog/exchange/exchange-online-ews-your-time-is-almost-up/4492361
- IT-Service Walter: *EWS-Abschaltung Exchange Online* (Timeline, Allowlist, 24-h-Propagierung) – https://www.der-windows-papst.de/2026/10/28/ews-abschaltung-exchange-online/
- IT-Consulting Blog: *EWS-Abschaltung in Exchange Online* – https://it-consulting.blog/2026/08/05/ews-abschaltung-in-exchange-online-was-unternehmen-jetzt-vorbereiten-sollten/
- NuGet: *Microsoft.Exchange.WebServices.NETStandard 1.1.3* – https://www.nuget.org/packages/Microsoft.Exchange.WebServices.NETStandard
- Spezifikation: [SPECIFICATION.md](SPECIFICATION.md) (v3, 30.10.2026)

---

## 16. Freigabe

| Rolle | Name | Datum | Unterschrift |
|---|---|---|---|
| Auftraggeber IT | | | |
| Projektleitung | | | |
| Tenant-Administration | | | |
| David-Administration | | | |
| Betrieb | | | |
| OSS-Maintainer | | | |

**Mit der Freigabe werden D-01…D-05 und D-12 als entschieden sowie D-06…D-11 und D-13 als Arbeitsgrundlage bestätigt; Änderungen erfolgen über den Änderungsprozess und werden im Entscheidungslog geführt.**
