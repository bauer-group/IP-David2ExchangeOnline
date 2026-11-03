# Archive entpacken für Fax und Voice

David packt ältere Archive. Bei gepackten Archiven liefern `FaxItem.Image` und `VoiceItem.Content` nur einen internen Containerpfad (`M[...].001`), der nicht als Datei lesbar ist. Für die Migration von Faxen und Voice-Nachrichten müssen die Archive entpackt sein. Ein gepacktes Archiv erkennt man an der Datei `ARCHIVE.PCK` im Archivordner. Auf dem gemessenen Server betrifft das ältere Teilarchive; das Haupt-`in` eines Benutzers ist zum Beispiel nicht gepackt.

## 1. Dry-Run

```powershell
.\david2exchange.exe unpack --config migration.yaml --dry-run
```

Zeigt alle `COMMON`- und `USER`-Archive rekursiv, ohne Änderungen.

## 2. Entpacken markieren

```powershell
.\david2exchange.exe unpack --config migration.yaml
```

Der Befehl setzt für jedes Archiv rekursiv:

```csharp
archive.Options.BundleFiles = 0;
archive.Save();
```

Er umfasst:

- `COMMON` (Unverteilt) inklusive aller Unterordner
- alle Benutzerarchive unter `USER` inklusive aller Unterordner

Der Befehl **entpackt nicht sofort**. Er setzt nur das Flag. Die tatsächliche Entpackung passiert bei der David-Datenbereinigung/Archiv-Optimierung.

## 3. David-Bereinigung durch den David-Administrator

Nach dem Befehl muss der David-Administrator die **Datenbereinigung/Archiv-Optimierung** auf dem Server starten. Erst danach liegen die Faxdateien als echte TIFF-Dateien im Archiv.

Empfohlene Reihenfolge:

1. `unpack --dry-run`
2. `unpack`
3. David-Datenbereinigung/Archiv-Optimierung starten
4. Warten, bis die Bereinigung abgeschlossen ist
5. Faxmigration erneut starten

## 4. Prüfen

Ein entpacktes Fax hat einen echten Dateipfad, z. B.:

```text
\\192.168.0.10\david\archive\8\M9EE3DC4.001
```

Die Datei beginnt mit dem TIFF-Header:

```text
49 49 2A 00
```

Bei gepackten Archiven existiert dieser Pfad nicht; das Fax bleibt dann `failed`.

## 5. Migration

```powershell
.\david2exchange.exe run --config migration.yaml --wave wave-1 --all --output ews
```

Die Migration warnt zusätzlich:

```text
WARNUNG: Faxe werden nur migriert, wenn die David-Archive entpackt sind.
```

## Grenzen

- `unpack` benötigt David-Rechte zum Ändern der Archivoptionen.
- Der Befehl ersetzt **nicht** die David-Bereinigung.
- Nach der Bereinigung kann das Entpacken je nach Archivgröße lange dauern.
- Gepackte Faxe und Voice-Nachrichten werden nicht als Text-EML migriert; sie bleiben sichtbar `failed`. Der nächste Lauf nach dem Entpacken überträgt sie.
