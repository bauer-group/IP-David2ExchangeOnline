# Release und Self-Contained-EXE

## Workflow

`.github/workflows/dotnet-release.yml` nutzt die BAUER-GROUP-Reusable-Workflows:

| Job | Zweck |
|---|---|
| `validate` | Build auf `windows-latest` (x86, .NET 10) |
| `release` | Semantic Release nach Conventional Commits; schreibt `Directory.Build.props` und `CHANGELOG.md` |
| `publish-binaries` | Self-contained Single-File `win-x86` als ZIP + SHA-256 am GitHub Release |
| `publish-raw-exe` | Dieselbe EXE direkt als `.exe` + `.sha256` am Release |

Auslöser:

- Push auf `main` (Release nur, wenn Conventional Commits einen Bump ergeben)
- Pull Request (nur Build)
- `workflow_dispatch` mit `force-release: true`

## Versionierung

- Einzige Versionsquelle: `Directory.Build.props` → `<Version>`.
- `semantic-release-dotnet` schreibt diese Datei; `AssemblyVersion`, `FileVersion` und `InformationalVersion` leiten sich davon ab.
- Konfiguration: `.github/config/release/semantic-release.json`.
- Commits: `feat:` → Minor, `fix:` → Patch, `feat!:`/`BREAKING CHANGE` → Major.

## Artefakte

Beispiel für Version `1.2.3`:

```text
david2exchange-1.2.3-win-x86.zip
david2exchange-1.2.3-win-x86.zip.sha256
david2exchange-1.2.3-win-x86.exe
david2exchange-1.2.3-win-x86.exe.sha256
```

Die EXE ist **self-contained** und **single-file**: .NET-Runtime und NuGet-Abhängigkeiten sind enthalten. Sie benötigt keine installierte .NET-Runtime, aber weiterhin:

- Windows x86 (DvApi32 ist 32-Bit-COM)
- installierten David-Client und angemeldeten Windows-Benutzer
- `pwsh` 7.4+ nur für `setup`/`targets`

## Lokal reproduzieren

```powershell
dotnet publish src\David2Exchange\David2Exchange.csproj `
  -c Release `
  -r win-x86 `
  --self-contained true `
  -p:Version=1.2.3 `
  -p:PlatformTarget=x86 `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish/win-x86
```

Ergebnis:

```text
publish/win-x86/david2exchange.exe
publish/win-x86/david2exchange.pdb
```

Die Setup-Skripte sind als Ressourcen eingebettet und werden zur Laufzeit nach `%TEMP%\david2exchange\<version>` extrahiert. Deshalb funktioniert `setup` auch aus der Single-File-EXE ohne `Setup`-Ordner.

Prüfen:

```powershell
.\publish\win-x86\david2exchange.exe --version
.\publish\win-x86\david2exchange.exe probe --mock
.\publish\win-x86\david2exchange.exe setup --extract-only
```

## Workflow-Voraussetzungen

- Repository-Secret `GITHUB_TOKEN` ist automatisch vorhanden.
- `permissions: contents: write` für Release-Upload.
- `bauer-group/automation-templates` muss für das Repository zugänglich sein.
- Es wird **kein** NuGet-Paket veröffentlicht; dieses Projekt ist eine Windows-x86-Anwendung, kein `dotnet tool`.

## Fehlerbehebung

| Problem | Ursache | Lösung |
|---|---|---|
| `publish/<rid>/david2exchange` fehlt | `assembly-name` ≠ `<AssemblyName>` | Beide auf `david2exchange` halten |
| `NETSDK1045` / falsches SDK | `global.json` | `10.0.100` mit `rollForward: latestFeature` |
| EXE startet nicht | falsche Architektur | `win-x86`, nicht `win-x64` |
| `setup` findet Skript nicht | alte EXE ohne Embedded Resources | neu bauen/publizieren |
| Release ohne Assets | `release-created != true` | Conventional Commit oder `force-release` |
| `gh release upload` 403 | fehlende `contents: write` | Workflow-Permissions prüfen |
