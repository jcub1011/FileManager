# FileManager

A per-machine file-automation tool. A headless engine watches, schedules, and runs **Jobs** that
copy and transform files from configured **Sources** to **Targets** according to user-defined
**Profiles** (JSON). Cross-platform (Windows + Linux), AOT-friendly .NET 10.

> The project began as a placeholder Avalonia file browser and is being rebuilt to the v2 design in
> `docs/specs/spec-draft-v2.md`, staged across `docs/specs/spec-v2-implementation/`.

## Projects

| Project | Purpose |
| --- | --- |
| `src/FileManager.Core` | Engine library: Profile model + validation, JSON (source-generated), config resolution, filesystem abstraction. |
| `src/FileManager.Contracts` | Dependency-free shared contracts (local IPC endpoint names; message DTOs in M6). |
| `tests/FileManager.Core.Tests` | xUnit tests (schema round-trip, validation, config paths). |

GUI and Service-host projects arrive in later milestones (M6/M7).

## Build & test

Requires the .NET 10 SDK.

```bash
dotnet build FileManager.slnx
dotnet test  FileManager.slnx
```

CI builds and tests on Windows and Linux (`.github/workflows/build.yml`).

## Status

Milestone 0 (foundations): solution restructure, the authoritative Profile schema as strongly-typed
AOT-safe records with validated JSON, per-OS config resolution, and the global `ServiceConfig`.
No file processing yet — that begins in Milestone 1.
