# Implementation Plan — File Pipeline Utility (spec v2)

This folder breaks [`../spec-draft-v2.md`](../spec-draft-v2.md) into an ordered set of **milestones**,
one file per milestone. Each milestone is independently buildable and verifiable, and depends only on
milestones before it.

> **Status of the repository today:** the codebase is a placeholder — a thin Avalonia 12 / .NET 10
> MVVM *file-browser* scaffold (single `FileManager` project, no tests, no CI). None of the spec's
> architecture exists yet, so this is effectively a greenfield build.

## How to use this plan

- Read milestones in order (M0 → M9). Each file states its **dependencies**, **scope**, a **task
  checklist**, the **proposed code structure**, and **acceptance criteria**.
- "Out of scope" items in each milestone name the later milestone that owns them, so nothing is lost.
- Spec section numbers (e.g. §3.3, Appendix B) refer to `spec-draft-v2.md`.

## Proposed solution architecture

Product namespace **`FilePipeline`** (matches the spec's IPC names, e.g. `filepipeline-<user>`). The
existing `src/FileManager` project is retired in M0; its reusable pieces migrate into the new
projects.

| Project | Role | First built in |
| --- | --- | --- |
| `src/FilePipeline.Core` | Engine library: Profile models, Job lifecycle, filters, transformers, verification, rollback, journal, watcher, scheduler, worker pool, trash/metadata. No UI deps; AOT-compatible. | M0 |
| `src/FilePipeline.Contracts` | IPC message contracts shared by Service/GUI/Shell (length-prefixed JSON DTOs). | M0 |
| `src/FilePipeline.Service` | Headless Core Service host: wires engine + IPC server + optional tray; autostart units. | M6 |
| `src/FilePipeline.Gui` | Avalonia configuration GUI (repurposed from `FileManager`): Profile editor, activity view, dry-run. | M7 |
| `src/FilePipeline.Shell` | Shell integration: Windows `IExplorerCommand` handler + registry fallback launcher; Linux file-manager actions; CLI fallback. | M8 |
| `tests/FilePipeline.Core.Tests` | Unit tests for the engine. | M0 |
| `tests/FilePipeline.Integration.Tests` | End-to-end acceptance tests (spec §12). | M9 |

## Milestones

| # | File | Title | Depends on |
| --- | --- | --- | --- |
| M0 | [milestone-0-foundations.md](milestone-0-foundations.md) | Foundations, solution restructure & Profile schema | — |
| M1 | [milestone-1-job-engine.md](milestone-1-job-engine.md) | Single-file Job engine (happy path) | M0 |
| M2 | [milestone-2-transformer-pipeline.md](milestone-2-transformer-pipeline.md) | Transformer pipeline | M1 |
| M3 | [milestone-3-data-safety.md](milestone-3-data-safety.md) | Data safety: verification, rollback, staging, mirror, metadata | M2 |
| M4 | [milestone-4-journal-recovery.md](milestone-4-journal-recovery.md) | Durable journal, crash recovery & audit trail | M3 |
| M5 | [milestone-5-triggers-concurrency.md](milestone-5-triggers-concurrency.md) | Triggers & concurrency | M4 |
| M6 | [milestone-6-service-ipc.md](milestone-6-service-ipc.md) | Core Service host, IPC, autostart & tray | M0, M5 |
| M7 | [milestone-7-gui-dryrun.md](milestone-7-gui-dryrun.md) | Configuration GUI, dry-run & observability | M6 |
| M8 | [milestone-8-shell-integration.md](milestone-8-shell-integration.md) | OS shell integration | M6, M7 |
| M9 | [milestone-9-security-network-acceptance.md](milestone-9-security-network-acceptance.md) | Security, network targets, non-functional & acceptance | M0–M8 |

## Dependency graph

```
M0 ─► M1 ─► M2 ─► M3 ─► M4 ─► M5 ─► M6 ─► M7 ─► M8
 │                                    ▲           │
 └────────────────────────────────────┘           │
                                                   ▼
M0…M8 ──────────────────────────────────────────► M9
```

`M0` also feeds `M6` directly (the `FilePipeline.Contracts` project). `M9` depends on everything.

## Spec-section coverage matrix

Every section and appendix of `spec-draft-v2.md` is owned by at least one milestone.

| Spec section | Owning milestone(s) |
| --- | --- |
| §0 Glossary (Profile/Job/Source/Target/Transformer/Engine terms) | M0 |
| §1 Product Overview | M0 (framing) |
| §1.1 Process model (service vs. tray) | M6 |
| §2 System Architecture (3 components) | M0 (structure), M6 (wiring) |
| §2.1 IPC transport (pipe/socket, length-prefixed JSON) | M0 (contracts), M6 (transport) |
| §3.1 Directory topologies (1:1/1:N/M:1/M:N) | M1 |
| §3.1.1 Sync mode — `AdditiveArchive` | M1 |
| §3.1.1 Sync mode — `Mirror` (+ trash deletions) | M3 |
| §3.1.2 Folder structure at target (`TargetLayout`) | M1 |
| §3.2 Execution triggers (manual / watcher / schedule) | M5 (watcher+schedule), M8 (manual shell + prompt) |
| §3.2.1 File readiness / settle policy | M5 |
| §3.2.2 Missed scheduled runs | M5 |
| §3.3 Transactional verification & rollback | M3 |
| §3.4 Conflict resolution | M1 |
| §4 End-to-end workflow — Phase 1 Ingestion & Journal open | M1 (lifecycle), M4 (journal) |
| §4 Phase 2 Filter screening | M1 |
| §4 Phase 3 Transformer chain | M2 |
| §4 Phase 4 Target distribution | M1 |
| §4 Phase 5 Verification & atomic placement | M1 (atomic placement), M3 (verification) |
| §4 Phase 6 Source disposition | M1 (basic), M3 (trash) |
| §5.1 Profile schema (JSON) + enum authority (incl. `OnFailure`, `Verbosity`) | M0 |
| Global `ServiceConfig` (`MaxWorkers` §5.4, allowlist §9, log/journal/audit locations §7) | M0 (model), M5/M6/M9/M4-M7 (consumers) |
| §5.2 Tokens | M1 (filename tokens), M2 (step tokens) |
| §5.3 OS integration (context menu, soft delete, autostart) | M3 (soft delete), M6 (autostart), M8 (context menu) |
| §5.4 Concurrency & locking (bounded pool, same-file lock) | M5 |
| §6.1 The one data-losing combination | M3 (detection), M7 (GUI warning) |
| §6.2 Overwrite handling (`DirectOverwrite` / `StageOverwrites`) | M1 (DirectOverwrite), M3 (StageOverwrites) |
| §6.3 Crash / restart recovery (durable journal) | M4 |
| §6.4 Metadata preservation | M3 |
| §7 Observability — persistent rotating log file | M1 (engine surface), M4 (rotating file writer) |
| §7 Observability — deletion audit trail | M4 |
| §7 Observability — in-GUI activity view + failure notifications | M7 |
| §8 Dry-run / simulation | M7 |
| §9 Security model | M2 (literal/shell argv), M9 (validation/allowlist/scoping) |
| §10 Network targets | M9 |
| §11 Non-functional requirements | M5 (watcher scale), M9 (throughput/footprint/streaming) |
| §12 Acceptance criteria | M9 |
| Appendix A (v1→v2 mapping) | informational — no milestone |
| Appendix B (open items to finalize) | flagged in M0; resolved across M1, M5, M6, M8, M9 |
| Appendix C (changes from v1) | informational — no milestone |
