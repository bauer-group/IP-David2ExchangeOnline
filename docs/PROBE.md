# David-Probe: Schritt für Schritt

Ziel dieser Stufe: **Beweisen, dass DvApi32 auf dem Migrationsrechner funktioniert** – Benutzerordner und Unverteilt, Metadaten und EML-Export. Es gibt noch keine EWS-Anbindung und keinen Versand.

## 1. Voraussetzungen

- David-Client (InfoCenter) installiert und mit dem Zielserver verbunden.
- Der angemeldete Windows-Benutzer ist in David angemeldet und hat Rechte auf die zu lesenden Archive.
- Der Prozess läuft als **x86**. Das ist bei `dotnet run`/`publish` mit `win-x86` automatisch der Fall.
- Optional: eine lokale Kopie des Repos und .NET 10 SDK.

## 2. Bauen und Mock testen

```powershell
dotnet build David2Exchange.slnx -c Debug
dotnet run --project src\David2Exchange -- probe --mock --out probe-out
```

Erwartung: zwei Mock-Archive, einige Items, EML-Dateien, Exit-Code 0. Damit sind CLI, Writer und Report getestet, ohne David anzufassen.

## 3. Eigenes Archiv öffnen

```powershell
dotnet run --project src\David2Exchange -- probe `
  --archive "user\@me" `
  --max-items 5 `
  --export-eml 2 `
  --out probe-out
```

`user\@me` wird über `Account.User.ID` aufgelöst. Auf SRV-BAUER war das `user\1000C000` (Dezimal-ID 268484608 → Hex `1000C000`).

Erwartung:

- `geöffnet: user\1000C000 (Karl Bauer)`
- Items aus `user\1000C000\Eingang` usw.
- EML-Dateien mit `Message-ID`, `Date`, `From`, `To`.

Wenn `GetArchive` „Insufficient rights“ liefert, aber der Baum geöffnet werden kann: Der Benutzer hat keine Rechte auf den direkten Pfad, oder der Pfad ist falsch. Der Adapter versucht dann `ArchiveRoot` → `GetSubArchive` und findet das Archiv über den Baum.

## 4. Unverteilt öffnen

```powershell
dotnet run --project src\David2Exchange -- probe `
  --archive "common\unverteilt" `
  --type 2 `
  --max-items 10 `
  --export-eml 3 `
  --out probe-out
```

Wichtig: `common\unverteilt` enthält auf SRV-BAUER auch Adress-Teilarchive (`#001#Adressen`, `ADDRESS\Mitarbeiter`). Ohne Filter erscheinen zuerst **Typ 1 = Adresse**. Für Mail **`--type 2`** verwenden. **Typ 7 = Kalender** (z. B. Einladungen im Eingang).

Alternative Filter:

```powershell
--exclude-path "ADDRESS" --exclude-path "#001#Adressen"
```

## 5. Was prüfen?

- [ ] `probe-report.json`: `Opened=true`, `ScannedItemCount > 0`, keine unerwarteten `Errors`.
- [ ] `items.jsonl`: `ArchivePath` zeigt den echten Unterordner, z. B. `user\1000C000\Eingang`.
- [ ] `Type=2` → `TypeLabel=mail`; `StatusTime`/`SendTime` sind gesetzt; `IsRead` plausibel.
- [ ] `AttachmentCount` plausibel; `From`/`To` lesbar.
- [ ] `eml/*.eml` öffnet in Outlook/Editor; `Message-ID` und `Date` stimmen.
- [ ] Anzahl der gelesenen Items passt zu `--max-items` (kein Vollscan in dieser Stufe).

## 6. Bekannte Befunde aus dem ersten Lauf

- `GetArchive("user\00000003")` ist **kein** gültiger Pfad; die Benutzer-ID ist eine Hex-ID, z. B. `1000C000`.
- `Account.User.ID` liefert die Dezimal-ID; `user\@me` löst sie auf.
- `common\unverteilt` enthält Adressarchive; `--type 2` filtert Mail.
- `Received` ist ein **Bool**, kein Datum. Datum kommt aus `StatusTime`/`SendTime`.
- `Viewed` ist der Lesestatus; `Attachments.Count` die Anhangzahl.
- `DVEmlFromMailItem` funktioniert mit der erwarteten Signatur.

## 7. Fehlerbilder

| Meldung | Bedeutung | Maßnahme |
|---|---|---|
| `ProgID ... nicht registriert` | David-Client fehlt oder Prozess ist x64 | x86-Prozess, Client installieren |
| `OLE_E_CANT_GETMONIKER` | David-Sitzung nicht verfügbar | InfoCenter starten/anmelden; Probe erneut |
| `Insufficient rights to archive` | Keine Rechte oder falscher Pfad | anderen Benutzer verwenden, `user\@me` prüfen |
| `DvApi32.dll nicht gefunden` | DLL nicht im Suchpfad | David-Client-Installation prüfen |
| `DVEmlFromMailItem ... nicht gefunden` | Signatur/Version abweichend | `SPIKE.md` ergänzen, Exportpfad prüfen |
| Typ 1 statt Mail | Adress-Teilarchiv | `--type 2` oder `--exclude-path` |

## 8. Echter EWS-Import (Mail)

Der EWS-Pfad ist **kein Mock**: `EwsOutputTarget` holt ein Token per MSAL-Zertifikat, impersoniert das Zielpostfach und importiert die EML mit `MessageDisposition.SaveOnly`.

Voraussetzungen:

- App-Registrierung mit `full_access_as_app` und Admin-Consent.
- Zertifikat in `CurrentUser\My`, Thumbprint bekannt.
- Zielpostfach existiert und ist in der Application Access Policy.
- Der Aufruf läuft als x86.

```powershell
dotnet run --project src\David2Exchange -- probe `
  --archive "user\@me" `
  --type 2 `
  --max-items 3 `
  --export-eml 3 `
  --output ews `
  --target-upn test@example.com `
  --tenant-id <tenant-id> `
  --client-id <app-id> `
  --cert-thumbprint <thumbprint>
```

Erwartung:

- `EWS verbunden: test@example.com · Zielordner 'David-Probe'`
- pro Mail eine `CreateItems`-Antwort `Success`
- `EWS: 3 Items importiert.`
- Im Zielordner `David-Probe` liegen die Mails als **empfangene** Mails, nicht als Entwurf.

Wenn das Zertifikat fehlt: Exit 2 mit klarer Meldung. Wenn die App keine Berechtigung hat: EWS-Fehler `ErrorAccessDenied`; dann `setup`/Consent/Allowlist prüfen.

## 9. Nächste Stufe

1. Typ-Enum vollständig dokumentieren (Mail, Fax, SMS, Chat, Voice, Kalender, Kontakt).
2. Fax/SMS/Voice/Chat-Exportmethoden prüfen.
3. `_ID`-Stabilität und Eindeutigkeit je Archiv prüfen.
4. Danach erst EWS-Adapter und Manifest anschließen.

