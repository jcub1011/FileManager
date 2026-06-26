# FileManager — repository guide

FileManager is a per-machine file-automation tool: a headless **engine** watches/schedules/runs
**Jobs** that copy and transform files from **Sources** to **Targets** according to user-configured
**Profiles**. The full design lives in `docs/specs/spec-draft-v2.md`; implementation is staged across
`docs/specs/spec-v2-implementation/milestone-*.md`.

## Solution layout

```
FileManager.slnx              Solution
Directory.Build.props          Shared net10.0 / Nullable / IsAotCompatible / analyzers
.editorconfig                  Code style: file-scoped namespaces, records, required members
global.json                    Pins the .NET 10 SDK band

src/FileManager.Core/         Engine library (the substance today)
  FileSystem/                  IFileSystemService / FileSystemService / FileSystemEntry
  Profiles/                    Profile model records, Enums, JSON source-gen, validator, store
  Configuration/               ConfigPaths, ServiceConfig, ServiceConfigStore
  Journal/                     Durable, fsync'd Job journal + crash-safe framing (M4)
  Audit/                       Append-only deletion audit trail (M4)
  Recovery/                    Startup crash recovery over OPEN journal entries (M4)
src/FileManager.Contracts/    Dependency-free shared contracts (IPC endpoint names today)
tests/FileManager.Core.Tests/ xUnit tests + Samples/profile-v2-sample.json
.github/workflows/build.yml    CI: build + test on windows-latest and ubuntu-latest
```

The original Avalonia `src/FileManager` GUI was retired in Milestone 0; only its filesystem
abstraction was migrated into `FileManager.Core`. The GUI and Service hosts return as their own
projects in M6/M7.

## Build & test

```bash
dotnet build FileManager.slnx          # build everything
dotnet test  FileManager.slnx          # run the test suite
dotnet build FileManager.slnx -c Release   # what CI runs
```

## Conventions

- **Target framework:** `net10.0`, `Nullable` enabled, `ImplicitUsings` enabled (set once in
  `Directory.Build.props` — individual csproj files stay minimal).
- **AOT-clean:** `IsAotCompatible=true` turns on the trim/AOT analyzers; reflection leaks surface as
  build warnings. Keep `FileManager.Core` and `FileManager.Contracts` dependency-free and free of
  reflection. **No reflection-based JSON** — all (de)serialization goes through the
  `System.Text.Json` source generator in `Profiles/ProfileJsonContext.cs` via the
  `ProfileSerializer` façade.
- **Style:** file-scoped namespaces, records for data models, `required` members for mandatory
  fields, `init`-only properties.
- **Validation, not exceptions:** schema/config problems are returned as `ValidationResult`
  (a list of `ValidationError`), never thrown. Loaders (`ProfileStore`, `ServiceConfigStore`) never
  throw on a bad file — they report per-file results.

## Profiles & configuration

- A **Profile** (`Profiles/Profile.cs`) is one JSON file under the config dir's `profiles/` folder.
  Property names are PascalCase to match the on-disk schema (spec §5.1) exactly. Enum values are the
  authoritative names from §5.1 (`Profiles/Enums.cs`).
- `ConfigPaths` resolves the per-OS config directory: Windows `%APPDATA%\FileManager\`; Linux
  `$XDG_CONFIG_HOME/filemanager/` (fallback `~/.config/filemanager/`).
- `ServiceConfig` (`config.json`, sibling to `profiles/`) holds service-scoped settings —
  `MaxWorkers`, the executable `Allowlist`, and log/journal/audit locations + rotation sizes — with
  documented defaults. Consumed by later milestones (M5/M6/M9, M4/M7).

> Note: STJ source-gen does not run record property initializers on deserialization, so
> `ServiceConfigStore` restores defaults for numeric keys absent from the JSON. Keep that in mind
> before relying on a `= default` initializer surviving a round-trip.

## Durable files (M4)

Three durable on-disk surfaces are resolved from `ServiceConfig` (each falls back to a default under
the config dir when its setting is null — closes the M0 Appendix B item):

- **Job journal** — `JournalDirectory` ?? `<config>/journal/`, file `jobs.journal`. Append-only,
  one framed record per lifecycle transition (`[len][CRC32][UTF-8 JSON]`), `fsync`'d per record.
  Rotated/compacted **in place** (closed-Job records dropped) once it exceeds `JournalRotationSizeBytes`.
  Decode caps a record at `JournalFraming.MaxRecordBytes` so a torn length prefix can't OOM recovery.
  Single-writer in M4; M5 owns concurrency. `FileJournal.FromConfig(...)`.
- **Deletion audit trail** — `AuditLogPath` ?? `<config>/deletions.audit`. Append-only, one compact
  JSON line per source disposition and per Mirror deletion that **actually occurred**
  (path/action/destination/timestamp/Job), `fsync`'d per entry, rotated to a timestamped backup.
  `AuditLog.FromConfig(...)`.
- **Application log** — `LogDirectory` ?? `<config>/logs/`, file `filemanager.log`. Rotating text log
  backing the M1 `ILogSink`; **verbosity is applied by `JobEngine` before `Log`, never re-applied
  here**. Best-effort (no per-line fsync). `RotatingLogWriter.FromConfig(...)`.

Durability seam: `IDurableAppendWriter` (`Append`/`Flush`→real fsync); `SystemDurableAppendWriter`
wraps `FileMode.Append` + `Flush(flushToDisk:true)`. Journal/audit JSON uses the compact
`DurableJsonContext` source-gen root (no reflection). `JobEngine` takes optional `IJournal`/`IAuditLog`
(default `NullJournal`/`NullAuditLog`); `RecoveryService.Recover()` cleans pre-placement Jobs and rolls
back mid-placement ones, and **never disposes a source unless `AllTargetsVerified` was journaled**.
