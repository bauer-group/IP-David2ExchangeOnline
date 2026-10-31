# SPIKE – DvApi32-Datenzugriff (Milestone 1)

Stand: 31.10.2026 · Rechner: Windows 11 · Prozess: x86 · David-Client installiert · Server: SRV-BAUER

## Ziel

Prüfen, ob der Datenzugriff über DvApi32 für **Benutzerordner** und **Unverteilt** funktioniert und ob Objekte (Metadaten, EML) abrufbar sind – zunächst **ohne EWS**, mit lokalem Writer.

## Ergebnis

**Ja, der Datenzugriff funktioniert.** Benutzerarchiv und Unverteilt lassen sich öffnen, Mail-Items lesen und als EML exportieren.

## Beobachtungen

### COM / Logon

| Punkt | Ergebnis |
|---|---|
| ProgID | `DVOBJAPILib.DvISEAPI` |
| Prozess | x86, `[STAThread]` |
| Logon | `Logon("", "", "", "", "", "AUTH")` funktioniert |
| Besonderheit | Logon kann sporadisch `OLE_E_CANT_GETMONIKER` liefern, wenn die David-Sitzung nicht bereit ist; Retry (3×, 1,5 s) hilft |
| `Account.ArchiveRoot` | liefert den Archivbaum (12 Top-Level-Archive) |
| `GetArchive("user\00000003")` | schlägt fehl: `Insufficient rights to archive` – Pfad ist falsch |
| `GetSubArchive("USER")` / `Archives` | funktioniert; Benutzerarchiv ist z. B. `user\1000C000` |
| `Account.User.ID` | Dezimal-ID (268484608) → Hex `1000C000`; daraus `user\1000C000` |

### Benutzerarchiv

- `user\1000C000` enthält u. a. `Eingang` (191.542 Items), `Ausgang` (56.504), `Kalender` (57.670).
- `AllItems` auf dem Benutzer-Root ist leer; Items liegen in den Unterarchiven.
- Der Adapter navigiert über `ArchiveRoot` → `GetSubArchive` und findet die Unterarchive.

### Unverteilt

- `common\unverteilt` öffnet; der Baum enthält u. a. `#001#Adressen`, `ADDRESS\Mitarbeiter`, `Unverteilt`.
- **Typ 1 = Adresse/Kontakt**, **Typ 2 = Mail**, **Typ 7 = Kalender** (auf diesem Server beobachtet).
- Ohne Filter erscheinen zuerst Adressen; für Mail `--type 2` verwenden.
- `common\unverteilt` mit `--type 2` liefert Mail-Items und EML-Export.

### MessageItem-Eigenschaften

| Eigenschaft | Typ | Bedeutung |
|---|---|---|
| `_ID` | Int32 | Item-ID im Archiv |
| `Type` | DvItemTypes (Int32) | 1 = Adresse, 2 = Mail, 7 = Kalender (beobachtet) |
| `Subject` | String | Betreff |
| `StatusTime` | DateTime | Empfangs-/Statuszeit |
| `SendTime` | DateTime | Sendezeit |
| `Received` | Boolean | empfangen ja/nein – **kein Datum** |
| `Viewed` | Boolean | gelesen |
| `Flags` | Int32 | Mail-Flags (z. B. 256) |
| `From` | Address | `EMail`, `DisplayName`, `FaxNumber`, … |
| `To` | String | Empfänger |
| `CC` | Addresses | Collection |
| `Attachments` | Attachments | Collection, `Count` |
| `VCard` | Variant | für Kontakte relevant |

### EML-Export

`DVEmlFromMailItem(item, StringBuilder)` mit `int`-Rückgabe funktioniert. Beispiel-EML enthält:

- `From`, `Subject`, `To`, `Date`
- `Message-ID`
- `MIME-Version`, `Content-Type`
- `X-David-*`-Header

### Weitere Beobachtungen

- `AllItems` kann Items aus Unterarchiven mitliefern; der Probe dedupliziert über `(ArchivePath, _ID)`.
- `GetSpecialArchive(DvArchiveTypes)` existiert; die Enum-Werte sind noch nicht gemappt.
- `GetArchive` und `GetSubArchive` verhalten sich bei Rechten unterschiedlich; der Baumweg ist robuster.
- Typ-Enum ist noch nicht vollständig bekannt (Fax, SMS, Voice, Chat, Kalender, Kontakt fehlen).

## EWS-Stand

`EwsOutputTarget` ist implementiert: MSAL-Zertifikat, app-only Token, Impersonation, `CreateItems` mit `MessageDisposition.SaveOnly`, Zielordner `David-Probe`. Der End-to-End-Test gegen ein echtes Testpostfach steht aus, weil in dieser Umgebung kein Tenant-Zertifikat vorliegt.

## Offene Spike-Punkte

- [ ] Vollständige `DvItemTypes`-Werte (Fax, SMS, Voice, Chat, Kalender, Kontakt).
- [ ] Exportmethoden für Fax-Bild, Voice-Audio, Chat-Verlauf.
- [ ] `_ID`-Eindeutigkeit und Stabilität je Archiv.
- [ ] Kontakt: `VCard`-Export vs. Feldmapping.
- [ ] Kalender: Serienfelder und Zeitzonen.
- [ ] Unverteilt: exakte Semantik und Spezialarchiv-Zugriff.
- [ ] Attachment-Export und Größenermittlung.

## Reproduktion

```powershell
dotnet build David2Exchange.slnx -c Debug
dotnet run --project src\David2Exchange -- probe --mock --out probe-out
dotnet run --project src\David2Exchange -- probe --archive "user\@me" --archive "common\unverteilt" --type 2 --max-items 10 --export-eml 5 --out probe-out
```

