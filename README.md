# david2exchange

Tobit David → Exchange Online migration tool (x86, .NET 10, real DvApi and EWS paths).

## What works today

- **DvApi32** connection (late binding, no typelib build dependency), `user\@me`, Unverteilt, archive tree walk.
- **LiteDB manifest** with item states (`discovered`, `exported`, `importing`, `imported`, `failed`, `skipped`), resume, limits.
- **Commands:** `sources`, `targets`, `map`, `discover`, `run`, `run --dry-run`, `report`, `probe`, `setup`.
- **Output ports:** `local` (JSONL + EML) and `ews` (real Exchange Online import via MSAL certificate + impersonation). Graph/PST are separate `IOutputTarget` adapters.
- **Setup scripts:** Entra app, certificate, `full_access_as_app`, admin consent, EWS allowlist backup/update/remove.

## Requirements

- Windows 10/11 or Server 2019+
- David client installed and connected; the Windows user must be signed in to David and have archive rights
- .NET 10 SDK (build) or the .NET 10 x86 runtime (run)
- x86 is mandatory: DvApi32 is 32-bit COM

## Build

```powershell
dotnet build David2Exchange.slnx -c Release
```

## Migration workflow

```powershell
# 1. Config automatisch erzeugen: Ziele als JSON, dann 1:1-Mapping
dotnet run --project src\David2Exchange -- targets --json > targets.json
dotnet run --project src\David2Exchange -- config create `
  --config migration.yaml --targets-file targets.json --domain example.com

# 1a. Quellen/Ziele prüfen, offene Mappings ergänzen
dotnet run --project src\David2Exchange -- sources --config migration.yaml
dotnet run --project src\David2Exchange -- map --config migration.yaml --list
dotnet run --project src\David2Exchange -- map --config migration.yaml `
  --source "user\1000C000" --target karl.bauer@example.com --id kbauer --kind user

# 1c. Optional: fehlende Shared Mailboxes anlegen + Vollzugriff für den Migrations-Admin
dotnet run --project src\David2Exchange -- mailboxes ensure `
  --config migration.yaml --wave wave-1 --admin-upn admin@example.com

# 2. Tenant setup (interactive; needs Global Admin / Exchange Admin)
dotnet run --project src\David2Exchange -- setup --config migration.yaml
dotnet run --project src\David2Exchange -- setup --config migration.yaml --check

# 3. Discover metadata for one migration wave
dotnet run --project src\David2Exchange -- discover --config migration.yaml --wave wave-1 --limit 20

# 4. Dry-run
dotnet run --project src\David2Exchange -- run --config migration.yaml --wave wave-1 --limit 20 --dry-run

# 5. Local test run
dotnet run --project src\David2Exchange -- run --config migration.yaml --wave wave-1 --limit 20 --output local

# 6. EWS run (after setup)
dotnet run --project src\David2Exchange -- run --config migration.yaml --wave wave-1 --limit 20 --output ews

# 7. Verify wave + report
dotnet run --project src\David2Exchange -- verify --config migration.yaml --wave wave-1
dotnet run --project src\David2Exchange -- report --config migration.yaml --out data\report.html
```

`user\@me` is resolved from `Account.User.ID` to the real hex archive path (for example `user\1000C000`).

## Probe (raw David access)

```powershell
dotnet run --project src\David2Exchange -- probe --mock --out probe-out
dotnet run --project src\David2Exchange -- probe `
  --archive "user\@me" --archive "common\unverteilt" `
  --type 2 --max-items 20 --export-eml 5 --out probe-out
```

## Portable layout

The tool is not installed. Keep it in one folder:

```text
david2exchange.exe
migration.yaml
data\manifest.db
logs\david2exchange-YYYYMMDD.log
```

Relative paths in `migration.yaml` are resolved from the config folder. The default config path is `migration.yaml` next to the EXE. All console output is mirrored to the daily text log.

## Output

| File | Content |
|---|---|
| `staging\manifest.db` | LiteDB state (runs/items) |
| `staging\output\items.jsonl` | migrated item records (local target) |
| `staging\output\eml\*.eml` | exported mails |
| `staging\report.html` / `.json` | report |
| `probe-out\probe-report.json` | probe summary |

## Validate

```powershell
pwsh scripts\validate.ps1
```

Builds Release, runs the mock probe, and — if David is installed — runs real `probe`, `discover`, `run` and `report` against `user\@me`.

## EWS output

`--output ews` is a real Exchange Online import. It needs an app registration with `full_access_as_app`, a certificate in `CurrentUser\My`, and a target mailbox. The adapter acquires a token via MSAL, impersonates the target UPN, creates the target folder, and imports EML with `MessageDisposition.SaveOnly`. It does not send mail.

The EWS round-trip still needs a test tenant certificate/mailbox; see [docs/PROBE.md](docs/PROBE.md).

## Release

`main` → `.github/workflows/dotnet-release.yml`:

- Build auf `windows-latest`
- Semantic Release (Conventional Commits, `Directory.Build.props`)
- Self-contained Single-File **win-x86** EXE als ZIP **und** direkt als `.exe` am GitHub Release, jeweils mit SHA-256

Details: [docs/RELEASE.md](docs/RELEASE.md).

## Safety

- No mail is sent, moved, or deleted.
- `--dry-run` writes no manifest item states and no target objects.
- Exit codes: `0` success, `1` warnings, `2` fatal, `3` usage error.

Documentation index: [docs/README.md](docs/README.md).

- [Configuration and mapping](docs/CONFIG.md)
- [Release and self-contained EXE](docs/RELEASE.md)
- [Probe on the David workstation](docs/PROBE.md)
- [Spike findings](docs/SPIKE.md)
- [Team plan](docs/TEAM-PLAN.md)
- Background: [Specification](docs/SPECIFICATION.md) · [Implementation plan](docs/IMPLEMENTATION-PLAN.md)

