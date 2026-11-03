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

Fax images: for **unpacked** David archives, `FaxItem.Image` is a real TIFF path. The tool remaps its `...\archive\...` suffix to `david.archiveRoot` and embeds the TIFF as `image/tiff`. `archiveRoot` may be either `\\server\david` or `\\server\david\archive`; both are tried. For **packed** archives, `FaxItem.Image` is only an internal container path; the file does not exist, so the fax is marked `failed` with that path instead of creating a text-only EML, even if David lists other attachments. Packed faxes require a server-side David API export/unpack. David keeps a fax's date and read state only in its `Fields` collection; the tool reads them from there, so a fax keeps its original date.

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
| E-mails | yes | **yes** | David EML preferred; revision-safe HTML/plain fallback with attachments if David export fails. A mail without native export and without readable text or attachment fails visibly. |
| Fax | yes | yes | Unpacked archive: TIFF from `FaxItem.Image` converted to a multi-page PDF (`fax-<nummer>-<datum>.pdf`); fallback subject is `[FAX] <nummer>` or `[FAX]`. Packed archive: fails visibly; no text-only EML. |
| SMS | yes | yes | Attachment-preserving EML fallback; no native `DVEmlFromMailItem` call |
| Chat | yes | yes | Attachment-preserving EML fallback; no native `DVEmlFromMailItem` call |
| Voice | yes | yes | EML with the original audio (`VoiceItem.Content` or attachment), caller as sender; **no transcription**. Not yet verified against a real voice item. |
| Calendar | yes | yes | EWS Appointment with HTML body; recurring series currently as single appointment. An appointment with attachments fails visibly, because the attachments are not transferred. |
| Contacts | yes | yes | EWS Contact field mapping (name, e-mail, phone, fax, mobile, notes) with HTML body. Attachments: as for calendar. |
| Tasks | yes | yes | EWS Task with subject, HTML/plain body, start/due date, status, priority, percent complete. Attachments: as for calendar. |
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

Subfolders below these roots are created with the same hierarchy. Root-level David items go to Inbox. The Exchange roots are resolved via **`WellKnownFolderName`** (Inbox, SentItems, Drafts, DeletedItems, JunkEmail, Calendar, Contacts, Tasks), so localized folder display names in the target mailbox do not matter. The mapping uses the numeric `DvArchiveTypes` value and additionally derives the root from the actual `ArchivePath` (`...\Eingang`, `...\Ausgang`), so stale/empty `FolderPath` values cannot move Inbox mail into Sent Items.

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

### Performance and transfer pipeline

```text
David thread (STA, single)              Workers (default 8, no COM)
walk → export EML → build batch  ──►  bounded queue  ──►  CreateItems → verify → manifest
```

- **David stays strictly single-threaded.** One STA thread owns every COM object. It reads the archive, exports the EML, builds batches and blocks when the queue is full. Workers only ever see manifest documents and EML files.
- **Export and transfer run at the same time**, mailbox by mailbox. The queue is bounded, so memory and staging disk stay small regardless of mailbox size.
- **Batches** hold up to `limits.batchSize` messages (default 20) and at most `limits.maxBatchMegabytes` of EML (default 20). A batch only ever contains items of one source archive and therefore one target folder. A larger single item is sent alone.
- **Workers:** `limits.workers` (default 8), each with its own `ExchangeService`. Microsoft recommends no more than 10 open requests per mailbox. `workers: 1` with `batchSize: 1` gives a slow single-item mode without a separate code path.
- **Bottleneck on slow links:** David file access dominates, not Exchange. Measured over a 60 ms VPN link: about 1.9 s per `DVEmlFromMailItem`. Run the tool in the same LAN as the David server.
- **Verification:** every saved item is read back by its `ItemId`. Parent folder and `david2exchange.ItemKey` must match, otherwise the item counts as failed. Items are never moved.
- **Errors** are classified per item and per request:
  - *Transient* (`ErrorServerBusy`, `ErrorExceededConnectionCount`, HTTP 429, ...): retried. A `BackOffMilliseconds` hint pauses all workers of the mailbox.
  - *Unknown outcome* (timeout, dropped connection, `ErrorTimeoutExpired`, HTTP 5xx including 503, unreadable response, unknown errors): the target folder is searched by `ItemKey` before anything is sent again. After a transport failure or an unreadable response the worker first waits 65 s, because Exchange may still be processing the batch. This narrows the window for duplicates but cannot close it completely.
  - *Permanent for one item* (`ErrorMessageSizeExceeded`, HTTP 400 and 413, ...): only that item fails. If one item makes the server reject a whole request, the batch is split up to isolate it, with a lookup before each item is sent again.
  - *Target unusable:* `ErrorQuotaExceeded` or impersonation errors stop this mailbox; rejected credentials and EWS policy blocks (HTTP 403, `X-EWS-Policy-Reason`) stop the run.
- **Long runs:** the OAuth token is fetched through the MSAL cache before each request and refreshed once on HTTP 401. A short outage of the token endpoint is retried; rejected credentials stop the run at once.
- **Retries:** after the first pass, transient failures get up to `limits.retries` further passes per mailbox.
- **Staging:** the file name derives from the full item key, because David IDs are only unique per archive. EMLs are written to a temporary name first and deleted after a confirmed import. Failed items keep their file for the next attempt.
- **Skipped archives:** archives linked into a user's tree from elsewhere (for example `resource\hol`) are not migrated into that mailbox; each one is logged.
- **Unreadable sources:** a COM read error never counts as "no data". An unreadable item or archive is logged and does not stop the others, but the mailbox then counts as failed and `verify` does not pass.
- `limits.limit` caps the items handled per mailbox and run; items already imported do not count, so repeated pilot runs make progress. Use `--all` for a full or delta run.

**Exit codes of `run`:** `0` everything transferred, `1` finished but items or mailboxes failed, `2` aborted, `3` invalid configuration or arguments.

## 6. Abort and resume

Every item has an explicit state in `data\manifest.db`:

```text
discovered → exported → importing → imported
                    ↘ failed (retried on every run)
```

- `importing` is stored **before** a batch is sent. After a crash this marks exactly the items that may have reached the server.
- Items with an earlier attempt are searched in the target folder by `ItemKey` before sending; first attempts cost no lookup.
- An item counts as imported only for the output that confirmed it, and for EWS only for that target mailbox. A `--output local` test does not block the later transfer to Exchange, and a changed `target.upn` transfers the mailbox again.
- Items are identified by their David file (`TextSource`), not by `_ID`: `_ID` is only a position in the archive and shifts after deletions or a David cleanup.
- Ctrl+C stops the run and lets running batches finish. A second Ctrl+C ends the process at once, for example when a David call hangs; a process kill also leaves a consistent manifest. Restart with the same `run` command.
- `--limit` is safe: only the selected number of pending items is processed.
- A manifest created before schema 2 is refused for `discover` and `run`, because items could share one EML file at that time and its `imported` state cannot be trusted. Back up and remove the manifest and `eml-stage`, check the target mailboxes, and start again.

`verify` is a manifest-level check for one target: no open or failed items for the selected wave, every imported item went to that target, and the latest run read each mailbox completely from its current source. The target is Exchange unless `--output local` says otherwise, so a local test run never counts as a finished migration. A limited pilot run after a full run makes `verify` fail until the next `run --all`. A target-folder count comparison via EWS is the next extension.

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
| `verify [--wave] [--output local\|ews]` | check for open/failed items and complete walks for one target (default `ews`) |
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
