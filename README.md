# david2exchange

Tobit David → Exchange Online migration tool for Windows x86. The tool runs on a workstation with the David client installed; it is not installed as a service.

**Current status:** mail, fax, SMS, voice, chat, calendar, contacts, tasks and David folder hierarchy are implemented. Mail uses David's original EML export (`DVEmlFromMailItem`) first; if that fails, a revision-safe HTML/plain fallback is generated. Fax uses the original TIFF from `FaxItem.Image` for unpacked archives; packed faxes fail visibly. SMS/chat/voice use attachment-preserving EML fallbacks. Calendar becomes an EWS appointment, contacts become EWS contacts, tasks become EWS tasks. Messages are written into the existing target folders (`Inbox`, `Sent Items`, `Drafts`, `Deleted Items`, `Junk Email`, `Calendar`, `Contacts`, `Tasks`) and their subfolders. The mail EWS path has been tested against a real tenant; calendar/task/contact EWS paths still need validation. See [Supported content](#supported-content).

## 1. Get the executable

### Option A — release download (recommended for running a migration)

Download the latest release from:

**https://github.com/bauer-group/IP-David2Exchange/releases/latest**

Use the self-contained single-file executable:

```text
david2exchange-<version>-win-x86.exe
david2exchange-<version>-win-x86.exe.sha256
```

It contains the .NET runtime and all NuGet dependencies. No .NET installation is required on the workstation. The ZIP contains the same EXE plus `README.md`, `LICENSE` and `CONFIG.md`.

Verify the download:

```powershell
(Get-FileHash .\david2exchange-<version>-win-x86.exe -Algorithm SHA256).Hash.ToLower()
Get-Content .\david2exchange-<version>-win-x86.exe.sha256
```

### Option B — build from source

```powershell
dotnet publish src\David2Exchange\David2Exchange.csproj `
  -c Release -r win-x86 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish\win-x86
```

Result: `publish\win-x86\david2exchange.exe` (x86, self-contained).

## 2. Portable folder layout

The tool is designed to live in one folder:

```text
david2exchange.exe
migration.yaml
data\manifest.db
data\output\
data\report.html
logs\david2exchange-YYYYMMDD.log
```

Relative paths in `migration.yaml` resolve from the config folder. The default config is `migration.yaml` next to the EXE. Console output is mirrored to the daily log file.

`david.server` may be a hostname or an IP address; it is used for display/reporting. `david.archiveRoot` may also use an IP-based UNC path, for example:

```yaml
david:
  server: 192.168.0.10
  archiveRoot: \\192.168.0.10\David\Archive
```

Fax images: for **unpacked** David archives, `FaxItem.Image` is a real TIFF path. The tool remaps its `...\archive\...` suffix to `david.archiveRoot` and embeds the TIFF as `image/tiff`. `archiveRoot` may be either `\\server\david` or `\\server\david\archive`; both are tried. For **packed** archives, `FaxItem.Image` is only an internal container path; the file does not exist, so the fax is marked `failed` with that path instead of creating a text-only EML. Packed faxes require a server-side David API export/unpack.

## 3. Quick start (EXE)

```powershell
# Version and basic check
.\david2exchange.exe --version

# Target mailboxes (User + Shared) once as JSON.
# Device-Code prompt goes to the console/stderr; the file contains only JSON.
.\david2exchange.exe targets --out targets.json

# Create migration.yaml and auto-map by e-mail address.
# Unknown users get <david-id>@<domain> as a shared retention mailbox.
.\david2exchange.exe config create `
  --config migration.yaml `
  --targets-file targets.json `
  --domain example.com `
  --wave wave-1 `
  --admin-upn admin@example.com

# Optional: create missing Shared Mailboxes and grant FullAccess
.\david2exchange.exe mailboxes ensure `
  --config migration.yaml --wave wave-1 --admin-upn admin@example.com

# Tenant setup: app registration, certificate, consent, EWS allowlist
# Login erfolgt per Device Code (URL + Code auf der Konsole)
.\david2exchange.exe setup --config migration.yaml
.\david2exchange.exe setup --config migration.yaml --check

# Inspect mapping
.\david2exchange.exe sources --config migration.yaml
.\david2exchange.exe map --config migration.yaml --list

# Metadata scan for one wave
.\david2exchange.exe discover --config migration.yaml --wave wave-1 --limit 20

# Dry-run, then real run
.\david2exchange.exe run --config migration.yaml --wave wave-1 --limit 20 --dry-run
.\david2exchange.exe run --config migration.yaml --wave wave-1 --limit 20 --output ews

# Verify wave and write report
.\david2exchange.exe verify --config migration.yaml --wave wave-1
.\david2exchange.exe report --config migration.yaml --out data\report.html
```

`--limit` is a test guard. Without `--limit`, `limits.limit` from `migration.yaml` applies. The example uses `0` (all); a value >0 limits the run. Use `--all` to override a configured limit.

### Mapping fallbacks

`config create` applies these rules:

| Source | Target |
|---|---|
| E-mail primary/alias matches a target | that target (user or shared) |
| User archive without David e-mail | `<david-id>@<domain>`, shared, `createIfMissing: true` |
| User archive with unmatched e-mail | source e-mail if it is a real domain, otherwise `<david-id>@<domain>`; shared |
| `user\00000000` (David system user) | mapping written with `enabled: false` |
| Any `COMMON` archive | **one** mapping for `COMMON` → `info@<domain>` (or `--unverteilt-upn`), shared, preserving all common subfolders |

`mailboxes ensure --config migration.yaml --wave wave-1 --admin-upn ...` then creates the missing shared mailboxes and grants FullAccess. The UPN is controlled by `--domain`, `--unverteilt-upn`, or a per-entry `target.upn` in YAML.

## 4. Supported content

| David content | Detection / probe | Migration | Notes |
|---|---|---|---|
| E-mails | yes | **yes** | David EML preferred; revision-safe HTML/plain fallback with attachments if David export fails |
| Fax | yes | yes | Unpacked archive: TIFF from `FaxItem.Image` converted to a multi-page PDF (`fax-<nummer>-<datum>.pdf`); fallback subject is `[FAX] <nummer>` or `[FAX]`. Packed archive: fails visibly; no text-only EML. |
| SMS | yes | yes | Attachment-preserving EML fallback; no native `DVEmlFromMailItem` call |
| Chat | yes | yes | Attachment-preserving EML fallback; no native `DVEmlFromMailItem` call |
| Voice | yes | yes | EML with the original WAV attachment; **no transcription** |
| Calendar | yes | yes | EWS Appointment with HTML body; recurring series currently as single appointment |
| Contacts | yes | yes | EWS Contact field mapping (name, e-mail, phone, fax, mobile, notes) with HTML body |
| Tasks | yes | yes | EWS Task with subject, HTML/plain body, start/due date, status, priority, percent complete |
| Folder tree | yes | yes | Inbox/Sent/Calendar/Contacts plus source subfolders |
| Read state | yes | yes | mail only |
| Deletions/moves in target | – | no | deliberately not synchronized |

Unsupported items are **not silently dropped**: they are written to the manifest as `skipped` with `last_error`/reason and appear in the report. See [docs/CONFIG.md](docs/CONFIG.md) for `include` and reporting.

Synthetic fallback EMLs contain **only the original body and attachments**. No “Migriert aus David” or similar text is appended, so the message content remains revision-safe. If David provides HTML, the fallback is `multipart/alternative` with both `text/plain` and `text/html`; formatting is not reduced to plain text. Provenance is kept in manifest/report metadata, not in the message body.

### Faxe: Archive vorher entpacken

Gepackte David-Archive liefern für Faxe nur einen internen Containerpfad, keine lesbare TIFF-Datei. Vor der Faxmigration:

```powershell
# 1. Nur anzeigen
.\david2exchange.exe unpack --config migration.yaml --dry-run

# 2. BundleFiles=0 für COMMON und alle USER-Archive setzen
.\david2exchange.exe unpack --config migration.yaml
```

3. **David-Datenbereinigung/Archiv-Optimierung durch den David-Administrator starten.**
4. Erst danach migrieren:

```powershell
.\david2exchange.exe run --config migration.yaml --wave wave-1 --all --output ews
```

`unpack` setzt nur `archive.Options.BundleFiles = 0` und speichert das Archiv. Die tatsächliche Entpackung passiert erst durch die David-Bereinigung. Details: [docs/UNPACK.md](docs/UNPACK.md).

### Folder mapping

| David archive root | Exchange folder |
|---|---|
| `in` / Eingang | Inbox |
| `out` / Ausgang | Sent Items |
| `draft` / Entwürfe | Drafts |
| `trash` / Gelöscht | Deleted Items |
| `spam` / Junk | Junk Email |
| `cal` / Kalender | Calendar (EWS Appointment) |
| `address` / Adressen | Contacts (EWS Contact) |
| `todo` / Aufgaben | Tasks |
| unknown / Unverteilt | Inbox with the source folder name as subfolder |

Subfolders below these roots are created with the same hierarchy. Root-level David items go to Inbox. The Exchange roots are resolved via **`WellKnownFolderName`** (Inbox, SentItems, Drafts, DeletedItems, JunkEmail, Calendar, Contacts, Tasks), so localized folder display names in the target mailbox do not matter. The mapping uses the numeric `DvArchiveTypes` value, not the localized David folder name.

## 5. Delta migration for active mailboxes

A migration can take days for large mailboxes. The tool is designed for repeated runs:

1. Every `run` performs a full metadata scan of the selected David archives.
2. New items (not yet in the LiteDB manifest) are added as `discovered`.
3. Already imported items are skipped.
4. New mail items are exported and imported in the next batch.
5. Deleted/moved David items are **not** deleted in Exchange; they are only reported.

Recommended operation:

```powershell
# Initial run per wave (can run overnight)
.\david2exchange.exe run --config migration.yaml --wave wave-1 --output ews

# Daily delta for active mailboxes: use --all (or limits.limit: 0)
.\david2exchange.exe run --config migration.yaml --wave wave-1 --all --output ews

# Unverteilt more frequently, e.g. hourly
.\david2exchange.exe run --config migration.yaml --mailbox unverteilt --output ews
```

Schedule via Task Scheduler under the David run user. Always run `setup --check` before a scheduled delta so an EWS outage is reported as an environment problem, not as item failures.

### Performance

- David export remains single-threaded (DvApi/COM is STA-bound) and fills the queue.
- EWS imports are sent in batches of **10** items (Microsoft EWS limit).
- Up to **8 EWS workers** import batches in parallel, each with its own `ExchangeService`.
- EWS request timeout is **60 seconds**.
- Throttling (`ErrorServerBusy`, `ErrorTimeoutExpired`, `ErrorBatchProcessingStopped`, transient HTTP/timeouts) is retried with exponential backoff and jitter; `BackOffMilliseconds` from EWS is honored.
- Failed batch items are retried individually, so a batch failure does not silently drop messages.
- Dedupe lookup is skipped for newly discovered items; it is only used for resume (`Exported`/`Importing`).
- LiteDB state is written after each completed EWS batch, not per item.
- Staged EMLs and attachment folders are deleted immediately after a successful batch/item import.
- On startup, staging files of already `imported` manifest items are cleaned up as well.
- Failed items keep their staging files for retry.
- `limits.limit` still caps the run; use `--all` for a full/delta run.

## 6. Abort and resume

Every item has an explicit state in `data\manifest.db`:

```text
discovered → exported → importing → imported
                    ↘ failed → skipped (after retries)
```

- Each item is committed individually.
- `exported` reuses the EML in `data\...`; it is not exported again.
- `importing` is retried on the next run.
- EWS checks the target folder by `InternetMessageId` before creating an item; an interrupted import therefore does not create a duplicate.
- `failed` is retried up to `limits.retries`; after that it becomes `skipped` and is reported.
- Ctrl+C or a process kill leaves a consistent manifest; restart with the same `run` command.
- `--limit` is safe: only the selected number of pending items is processed.

`verify` is a manifest-level check: no open or failed items for the selected wave. A target-folder count comparison via EWS is the next extension.

## 7. Tenant setup

Two paths are documented in [docs/SETUP.md](docs/SETUP.md):

- **Automatic:** `david2exchange setup` creates/updates the Entra app, certificate, `full_access_as_app` consent, and adds the app ID to `EwsAllowedAppIDs` with backup/verification.
- **Manual fallback:** Entra app registration, API permission, certificate, admin consent, EWS allowlist, Application Access Policy and verification commands.

`setup --check` verifies the current state; `setup --remove` removes the app, consent, certificate and allowlist entry without touching foreign app IDs or `EwsEnabled`.

`mailboxes ensure` creates missing **Shared Mailboxes**, sets aliases and grants `FullAccess` to the migration admin. User mailboxes are not created automatically because they require licensing.

## 8. Commands

| Command | Purpose |
|---|---|
| `setup [--check\|--remove]` | tenant app/certificate/consent/EWS allowlist |
| `config create` | list sources/targets and auto-map by e-mail |
| `sources [--json]` | list David user/common archives incl. e-mail |
| `targets [--json]` | list Exchange User/Shared mailboxes incl. aliases |
| `map [--list]` | manual mapping correction |
| `mailboxes ensure` | create missing Shared Mailboxes, aliases, FullAccess |
| `discover` | metadata scan into manifest |
| `run [--dry-run] [--wave] [--limit] [--output local\|ews]` | export/import; initial and delta |
| `verify [--wave]` | check for open/failed items |
| `report [--out]` | HTML/JSON report |
| `unpack [--dry-run]` | mark all COMMON/USER archives as unpacked (`BundleFiles=0`) |
| `probe` | low-level David/EWS diagnostics |

All commands run from the EXE; `--config` defaults to `migration.yaml` next to the EXE.

## 9. Build, tests, release

- Build: see section 1B or [docs/RELEASE.md](docs/RELEASE.md).
- Validation: `pwsh scripts\validate.ps1` (build + mock + real David probe if installed).
- CI: `.github/workflows/dotnet-release.yml` builds, semantic-releases and attaches self-contained `win-x86` EXE/ZIP plus SHA-256.
- Versioning: `Directory.Build.props`, driven by semantic-release.

## 10. Documentation

- [Configuration and mapping](docs/CONFIG.md)
- [Tenant setup: automatic and manual](docs/SETUP.md)
- [Release and EXE](docs/RELEASE.md)
- [Probe on the David workstation](docs/PROBE.md)
- Background: [Specification](docs/SPECIFICATION.md) · [Implementation plan](docs/IMPLEMENTATION-PLAN.md)
