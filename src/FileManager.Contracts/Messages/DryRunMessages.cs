namespace FileManager.Contracts.Messages;

/// <summary>
/// A request to preview (without acting) what processing a path would do (spec §2 / §8 dry-run). The
/// service resolves the Profile (by <see cref="ProfileId"/>, else by matching the path to a Profile's
/// Source), enumerates candidate files under <see cref="Path"/> read-only, runs the dry-run engine, and
/// returns a populated <see cref="DryRunReport"/> — making zero filesystem changes.
/// </summary>
/// <param name="Path">The absolute path that would be processed (a file or a directory root).</param>
/// <param name="ProfileId">Optional Profile id to preview under (null = resolve by Source).</param>
/// <param name="Recursive">Whether a directory path would be enumerated recursively.</param>
public sealed record DryRunRequest(string Path, string? ProfileId, bool Recursive);

/// <summary>One file the dry-run screened in, with its deciding filter (§8). Wire DTO (strings only).</summary>
/// <param name="SourcePath">Absolute path of the matching source file.</param>
/// <param name="RelativePath">Path relative to the owning Source root.</param>
/// <param name="DecidingFilter">The rule that decided inclusion (always <c>Pass</c> for matches).</param>
public sealed record DryRunMatchDto(string SourcePath, string RelativePath, string DecidingFilter);

/// <summary>One file the dry-run screened out, with the deciding filter rule (§8). Wire DTO.</summary>
/// <param name="SourcePath">Absolute path of the rejected source file.</param>
/// <param name="DecidingFilter">The filter rule that rejected the file.</param>
/// <param name="Detail">Optional rule-specific detail (e.g. the matched pattern).</param>
public sealed record DryRunScreenedOutDto(string SourcePath, string DecidingFilter, string? Detail);

/// <summary>
/// One fully token-expanded Transformer command preview (§8) — exactly what the engine would launch,
/// with no process started. Wire DTO carrying primitives/strings only.
/// </summary>
/// <param name="SourcePath">The source file this preview was computed for.</param>
/// <param name="Step">The step's 1-based position in the chain.</param>
/// <param name="Name">The step's display name.</param>
/// <param name="ExecutablePath">The executable that would be launched (the shell, for Shell mode).</param>
/// <param name="Literal">True for Literal mode (argv); false for Shell mode.</param>
/// <param name="Arguments">Expanded argv (Literal) or { shell-flag, command } (Shell).</param>
public sealed record DryRunCommandDto(
    string SourcePath,
    int Step,
    string Name,
    string ExecutablePath,
    bool Literal,
    IReadOnlyList<string> Arguments);

/// <summary>
/// One planned Target write (§8): the resolved final path and the conflict action as a string (e.g.
/// <c>Written</c>, <c>Overwritten</c>, <c>RenamedSuffix</c>, <c>Skipped</c>). Wire DTO.
/// </summary>
/// <param name="SourcePath">The source file (post-transform name) that would be written.</param>
/// <param name="TargetRoot">The Target root directory.</param>
/// <param name="FinalPath">The resolved destination path, or null when skipped.</param>
/// <param name="Action">The conflict-resolution action name.</param>
public sealed record DryRunTargetWriteDto(
    string SourcePath,
    string TargetRoot,
    string? FinalPath,
    string Action);

/// <summary>One surplus Target file a Mirror reconciliation would route to trash (§8). Wire DTO.</summary>
/// <param name="TargetRoot">The Target root the surplus file lives under.</param>
/// <param name="FilePath">The absolute path that would be removed.</param>
/// <param name="RelativeKey">The layout-relative key that made it surplus.</param>
public sealed record DryRunDeletionDto(string TargetRoot, string FilePath, string RelativeKey);

/// <summary>The planned source disposition for one matched file (§8). Wire DTO.</summary>
/// <param name="SourcePath">The source file that would be disposed.</param>
/// <param name="Action">The disposition action name (KeepSource / MoveToTrash / MoveToArchive / PermanentDelete).</param>
/// <param name="DestinationFolder">The destination folder for a move disposition; null otherwise.</param>
public sealed record DryRunDispositionDto(string SourcePath, string Action, string? DestinationFolder);

/// <summary>
/// The structured result of a <see cref="DryRunRequest"/> (spec §8): every matched file with its
/// deciding filter, every screened-out file, every fully token-expanded Transformer command, every
/// planned Target write (with action), every planned Mirror deletion, and the planned source
/// disposition per file. Self-contained (records of primitives/strings) so
/// <see cref="FileManager.Contracts"/> stays dependency-free; the service maps the Core domain report
/// onto this wire shape. <see cref="Implemented"/> is true once a real preview was produced; the M6
/// stub returns false via <see cref="NotImplemented"/>.
/// </summary>
public sealed record DryRunReport
{
    /// <summary>True when a real dry-run engine produced this report; false for the not-implemented stub.</summary>
    public required bool Implemented { get; init; }

    /// <summary>The Profile id the preview ran under (empty for the stub / when unresolved).</summary>
    public required string ProfileId { get; init; }

    /// <summary>Files that passed every filter, each with the deciding filter.</summary>
    public required IReadOnlyList<DryRunMatchDto> Matches { get; init; }

    /// <summary>Files rejected by a filter, with the deciding rule + detail.</summary>
    public required IReadOnlyList<DryRunScreenedOutDto> ScreenedOut { get; init; }

    /// <summary>Per-step, per-file fully-expanded Transformer command previews.</summary>
    public required IReadOnlyList<DryRunCommandDto> Commands { get; init; }

    /// <summary>Planned Target writes (one per matched file × Target), with the conflict action.</summary>
    public required IReadOnlyList<DryRunTargetWriteDto> TargetWrites { get; init; }

    /// <summary>Planned Mirror deletions (empty unless the Profile is in Mirror mode).</summary>
    public required IReadOnlyList<DryRunDeletionDto> Deletions { get; init; }

    /// <summary>Planned source disposition per matched file.</summary>
    public required IReadOnlyList<DryRunDispositionDto> Dispositions { get; init; }

    /// <summary>A human-readable note (e.g. the not-implemented marker, or a resolution error).</summary>
    public string? Note { get; init; }

    /// <summary>The not-implemented stub: <see cref="Implemented"/> false, empty sections, with a note.</summary>
    public static DryRunReport NotImplemented() => Empty(string.Empty, "Dry-run preview is not implemented.");

    /// <summary>An empty report for <paramref name="profileId"/> carrying an explanatory <paramref name="note"/>.</summary>
    public static DryRunReport Empty(string profileId, string? note) => new()
    {
        Implemented = false,
        ProfileId = profileId,
        Matches = Array.Empty<DryRunMatchDto>(),
        ScreenedOut = Array.Empty<DryRunScreenedOutDto>(),
        Commands = Array.Empty<DryRunCommandDto>(),
        TargetWrites = Array.Empty<DryRunTargetWriteDto>(),
        Deletions = Array.Empty<DryRunDeletionDto>(),
        Dispositions = Array.Empty<DryRunDispositionDto>(),
        Note = note,
    };
}
