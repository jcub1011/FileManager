# Milestone 0 — Progress

Branch: `feature/milestone-0-foundations` (off `feature/spec-v2-implementation`).
Plan: `~/.claude/plans/i-want-you-to-compressed-zephyr.md`. **Do not commit until user approves.**

SDK: .NET 10.0.301 available locally.

## Status — COMPLETE (awaiting review)

- [x] 1. Solution & build scaffolding (Directory.Build.props, .editorconfig, global.json, Core + Contracts csproj, FileManager.slnx)
- [x] 2. Migrate filesystem files into FileManager.Core/FileSystem; delete rest of src/FileManager
- [x] 3. Profile model records + enums (§5.1)
- [x] 4. JSON source-gen context (AOT-safe enums, preserve nulls)
- [x] 5. ProfileValidator (structured errors)
- [x] 6. Config paths & stores (ConfigPaths, ProfileStore, ServiceConfig, ServiceConfigStore)
- [x] 7. IPC name constants (FileManager.Contracts/IpcNames.cs)
- [x] 8. Tests (round-trip, validation, config paths, ServiceConfig) — 24 tests, all green
- [x] 9. CI workflow (.github/workflows/build.yml) — Windows + Linux matrix
- [x] 10. Docs (CLAUDE.md added / README.md rewritten)

## Verification done
- `dotnet build FileManager.slnx -c Release --no-incremental` → 0 warnings, 0 errors (AOT analyzers active via IsAotCompatible).
- `dotnet test FileManager.slnx` → Passed 24 / Failed 0.
- §5.1 sample round-trips (null-insensitive deep-equal) + serializer is a stable fixpoint.

## Notes / decisions
- GUI (`src/FileManager`) fully retired; only the 3 filesystem files migrated to Core.
- PascalCase JSON property names to keep the §5.1 sample round-trip faithful.
- Generic `JsonStringEnumConverter<T>` (source-gen friendly) — not the reflection-based one.
- Nulls preserved on write (sample contains explicit nulls); round-trip test normalizes
  explicit-null vs omitted as equivalent.
- **STJ source-gen does not run record property initializers on deserialize** → `ServiceConfigStore`
  restores defaults for numeric keys absent from JSON (distinguishes omitted from invalid). Covered
  by `LoadFrom_OmittedFields_FallBackToDefaults`.

## Open items deferred (per milestone)
- Appendix B path-format rules (UNC, long paths, ~/env expansion) → M1.
- Exact log/journal/audit locations + rotation sizes finalized in M4/M7 (placeholder defaults now).

## Log
- Branch + task list created; SDK confirmed net10.0.
- Scaffolding, migration, model, enums, JSON, validator, config, stores, IPC, tests, CI, docs done.
- All tests green; clean release build with no AOT warnings.
