# Team-Plan: parallel, schnell, validiert

Die Einzelentwickler-Kurve ist nicht mehr die Planungsgrundlage. Verbindlich ist die **Team-Kurve**: mehrere Entwickler und Coding-Agenten arbeiten gleichzeitig in getrennten Streams; ein Integrator hält `main` grün. Jeder Stream wird nach demselben Loop validiert, bevor er gemergt wird.

## 1. Rollen

| Rolle | Aufgabe |
|---|---|
| Integrator | `main`/`integ` grün halten, Merges, Konflikte, tägliche Builds |
| Stream A – David | DvApi-Typen, Exporter, MimeSynth, lokale Fixtures |
| Stream B – Core | Manifest/LiteDB, Zustandsautomat, CLI, Report |
| Stream C – Target | EWS/Auth/Ordner/Mail/Kalender/Kontakte, Setup |
| Stream D – Quality | Tests, CI, OSS-Doku, Public-Repo-Hygiene |
| Reviewer | Menschliches Review je PR; Agents mergen nie selbst nach `main` |

## 2. Branches und Dateien

| Stream | Branch | Darf ändern | Darf nicht ändern |
|---|---|---|---|
| A | `feat/david-*` | `Source/David`, `Source/Mock`, `Probe` | `Program.cs`, Interfaces, csproj |
| B | `feat/core-*` | `Manifest`, `Config`, `Domain`, `Cli`, `Reporting` | `Source/David`, `Target` |
| C | `feat/ews-*` | `Target`, `Setup` | `Source`, `Manifest` |
| D | `feat/quality-*` | `tests`, `docs`, `.github`, `scripts` | Produktionscode |
| Integrator | `integ` | alles | – |

Gemeinsame Dateien (`Program.cs`, Interfaces, `Directory.Packages.props`, `csproj`) gehören dem Integrator. Änderungen daran werden vorab abgestimmt.

## 3. Täglicher Loop

```powershell
# 1. Branch bauen und Mock-Probe
dotnet build David2Exchange.slnx -c Release
dotnet run --project src\David2Exchange -c Release --no-build -- probe --mock --out probe-out

# 2. Gesamtvalidierung (Build + Mock + optional echter David)
pwsh scripts\validate.ps1

# 3. Nur wenn grün: PR nach integ, Integrator merged nach main
```

**Stop-the-line:** Ist `main` rot, wird kein neuer Stream gemergt, bis der Integrator den Build repariert hat. Keine Ausnahmen.

## 4. Streams und Lieferobjekte

| Stream | Lieferobjekt | Validierung |
|---|---|---|
| A | Typ-Mapping 1/2/7 + Fax/SMS/Voice/Chat + `_ID`-Stabilität + EML/JSON-Export | realer Probe gegen David, `items.jsonl`, EML öffnet |
| B | LiteDB-Manifest, Zustandsautomat, Resume, Lock, Backup, Report | Unit-/Mock-Tests, Kill/Resume, CSV/JSON |
| C | `IOutputTarget`-Adapter (EWS, später Graph/PST), Auth, Ordner, Mail-Batches, Kalender, Kontakte, Setup | Test-Tenant, 100 Items, Message-ID 1:1; lokaler Port-Test |
| D | CI, `validate.ps1`, OSS-Doku, Public-Repo-Scan | CI grün, Clean-Clone-Build |

## 5. Zeitplan (Zielkorridor)

| KW | Zeitraum | Inhalt | Gate |
|---|---|---|---|
| 45 | 02.–06.11.2026 | Skeleton + DvApi-Probe (erledigt) | G0 |
| 46 | 09.–13.11.2026 | Streams A–D parallel; Spike S0–S5; tägliche Merges | G1 |
| 47 | 16.–20.11.2026 | Integration, Pipeline, RC1, interne Abnahme | – |
| 48 | 23.–27.11.2026 | Pilot 3 Postfächer, Fixes | G2 |
| 49 | 30.11.–04.12.2026 | Rollout Start, tägliche Deltas | – |
| 50 | 07.–11.12.2026 | Cutover-Zielkorridor | G3 |
| 51 | 14.–18.12.2026 | Hypercare / Rollback-Reserve | – |
| 52–53 | 21.12.2026–01.01.2027 | Feiertage / Puffer | – |
| 02–13 | 04.01.–31.03.2027 | Reserve, Nachmigration, Abbau | G4 |

**Wenn ein Gate rutscht:** Cutover verschiebt sich in KW 02/2027; der harte Termin 01.04.2027 bleibt mit ausreichend Puffer. Kein „weiterbauen ohne Gate".

## 6. Was die Parallelität nicht beschleunigt

- Tenant-Freigabe (G0) und echte EWS-Tests.
- Pilot und Abnahme durch Key User.
- Cutover und Mailfluss.
- Menschliches Review der Agenten-PRs.

Diese Punkte sind seriell und bestimmen den kritischen Pfad. Deshalb werden sie früh eingeplant, nicht am Ende.

## 7. Aktueller Stand (31.10.2026)

**Fertig und validiert:**

- x86-Solution, CLI `probe`, Mock-Modus, lokaler Writer.
- Echter DvApi-Zugriff: `user\@me` → `user\1000C000`, `common\unverteilt`, Typ 1/2/7, EML-Export.
- `scripts/validate.ps1`: Build + Mock + optional echter David; zuletzt **VALIDATION PASSED**.
- CI-Workflow, `README.md`, `docs/PROBE.md`, `docs/SPIKE.md`.
- `IOutputTarget` mit `LocalFileOutputTarget` und **echtem `EwsOutputTarget`** (MSAL-Zertifikat, Impersonation, Mail-Import); Graph/PST sind weitere Adapter.
- **LiteDB-Manifest** mit States, `discover`, `run`, `run --dry-run`, `report`; real gegen David mit 3 Items validiert.
- **Setup-Skripte** für App/Cert/Consent/Allowlist inkl. `--check`/`--remove`; Tenant-Test offen.
- **Offen:** EWS-End-to-End-Test gegen ein Testpostfach (Zertifikat/Tenant), Ordner-Mapping, Kalender/Kontakte, vollständige Typen (Fax/SMS/Voice/Chat).

**Nächste Streams:**

- A: Fax/SMS/Voice/Chat-Export, `_ID`-Stabilität, Attachments.
- B: LiteDB-Manifest, Resume, Lock, Backup.
- C: EWS-Testpostfach, Ordner-Mapping, Kalender/Kontakte, Setup.
- D: Tests, OSS-Release, Public-Repo-Scan.

## 8. Definition of Done je Stream

1. Build x86 grün, 0 Warnungen.
2. Mock-Probe grün; bei David-Stream zusätzlich realer Probe.
3. Report/Logs ohne PII.
4. Doku/SPIKE aktualisiert.
5. Review durch Integrator oder Reviewer.
6. Keine internen Identifier im Public-Repo-Teil.

