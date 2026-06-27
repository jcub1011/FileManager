using FileManager.Core.Profiles;
using FileManager.Core.Routing;

namespace FileManager.Core.Simulation;

/// <summary>
/// A file the dry-run screened <b>in</b> (it passed every filter), recorded with the deciding filter so
/// the §8 report can show why it matched. For a passing file the engine records the
/// <see cref="FilterDecision"/> sentinel "Pass" as the deciding filter.
/// </summary>
/// <param name="SourcePath">Absolute path of the matching source file.</param>
/// <param name="RelativePath">Path relative to the owning Source root (drives Target layout).</param>
/// <param name="DecidingFilter">The rule that decided inclusion (always <c>"Pass"</c> for matches).</param>
public sealed record DryRunMatch(string SourcePath, string RelativePath, string DecidingFilter);

/// <summary>
/// A file the dry-run screened <b>out</b>, with the deciding filter rule and any rule-specific detail
/// (e.g. the offending exclude pattern), mirroring <see cref="FilterDecision"/> exactly.
/// </summary>
/// <param name="SourcePath">Absolute path of the rejected source file.</param>
/// <param name="DecidingFilter">The filter rule that rejected the file.</param>
/// <param name="Detail">Optional rule-specific detail (e.g. the matched pattern).</param>
public sealed record DryRunScreenedOut(string SourcePath, string DecidingFilter, string? Detail);

/// <summary>
/// The fully token-expanded command one Transformer step <b>would</b> run for one source file — the
/// preview that, by construction, equals what <c>TransformerRunner</c> would actually launch (no
/// process is started). <see cref="Literal"/> distinguishes the two argument modes: a Literal step
/// reports its argv (one entry per element); a Shell step reports the single shell command line.
/// </summary>
/// <param name="SourcePath">The source file this step preview was computed for.</param>
/// <param name="Step">The step's 1-based position in the chain.</param>
/// <param name="Name">The step's display name.</param>
/// <param name="ExecutablePath">The executable that would be launched (the shell, for Shell mode).</param>
/// <param name="Literal">True for <see cref="ArgumentMode.Literal"/> (argv); false for Shell.</param>
/// <param name="Arguments">
/// For Literal mode, the expanded argv elements. For Shell mode, exactly two elements: the shell flag
/// (e.g. <c>/c</c> or <c>-c</c>) and the single expanded command string — matching the
/// <c>ProcessLaunchSpec.Arguments</c> the runner would build.
/// </param>
public sealed record DryRunCommandPreview(
    string SourcePath,
    int Step,
    string Name,
    string ExecutablePath,
    bool Literal,
    IReadOnlyList<string> Arguments);

/// <summary>
/// A planned write to one Target for one source file: the resolved final path and the
/// <see cref="TargetAction"/> the live <c>ConflictResolver</c> chose (Written / Overwritten /
/// RenamedSuffix / Skipped). For a Skip the <see cref="FinalPath"/> is null.
/// </summary>
/// <param name="SourcePath">The source file (post-transform name) that would be written.</param>
/// <param name="TargetRoot">The Target root directory.</param>
/// <param name="FinalPath">The resolved destination path, or null when the write is skipped.</param>
/// <param name="Action">The conflict-resolution action (e.g. overwrite vs rename).</param>
public sealed record DryRunTargetWrite(
    string SourcePath,
    string TargetRoot,
    string? FinalPath,
    TargetAction Action);

/// <summary>
/// A surplus Target file that a <see cref="SyncMode.Mirror"/> reconciliation would route to trash
/// (never hard-delete) — mirroring <c>MirrorPlanner.Plan</c> exactly.
/// </summary>
/// <param name="TargetRoot">The Target root the surplus file lives under.</param>
/// <param name="FilePath">The absolute path that would be removed.</param>
/// <param name="RelativeKey">The layout-relative key that made it surplus.</param>
public sealed record DryRunDeletion(string TargetRoot, string FilePath, string RelativeKey);

/// <summary>
/// What source disposition <b>would</b> happen to one matched file on success — the would-be
/// <see cref="OnSuccess"/> action and (for moves) the destination folder. Computed by the shared
/// <c>SourceDisposer.PreviewDisposition</c> decision so the preview cannot drift from the live move.
/// </summary>
/// <param name="SourcePath">The source file that would be disposed.</param>
/// <param name="Action">The disposition action (KeepSource / MoveToTrash / MoveToArchive / PermanentDelete).</param>
/// <param name="DestinationFolder">
/// For <see cref="OnSuccess.MoveToTrash"/> / <see cref="OnSuccess.MoveToArchive"/>, the folder the file
/// would move into. Null for <see cref="OnSuccess.KeepSource"/> and <see cref="OnSuccess.PermanentDelete"/>.
/// </param>
public sealed record DryRunDisposition(string SourcePath, OnSuccess Action, string? DestinationFolder);

/// <summary>
/// The complete, side-effect-free preview of a run (spec §8): every matched file with its deciding
/// filter, every screened-out file, every fully token-expanded Transformer command, every planned
/// Target write (with its <see cref="TargetAction"/>), every planned Mirror deletion, and the planned
/// source disposition per matched file — plus headline counts. Produced by <see cref="DryRunEngine"/>
/// purely from reads; the engine writes, moves, and deletes nothing.
/// </summary>
public sealed record DryRunReport
{
    /// <summary>The Profile id the preview ran under.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Files that passed every filter (each carries the deciding filter, always "Pass").</summary>
    public required IReadOnlyList<DryRunMatch> Matches { get; init; }

    /// <summary>Files rejected by a filter, with the deciding rule + detail.</summary>
    public required IReadOnlyList<DryRunScreenedOut> ScreenedOut { get; init; }

    /// <summary>Per-step, per-file fully-expanded Transformer command previews (empty for copy Profiles).</summary>
    public required IReadOnlyList<DryRunCommandPreview> CommandPreviews { get; init; }

    /// <summary>Planned Target writes (one per matched file × Target), with the conflict action.</summary>
    public required IReadOnlyList<DryRunTargetWrite> TargetWrites { get; init; }

    /// <summary>Planned Mirror deletions (empty unless <see cref="SyncMode.Mirror"/>).</summary>
    public required IReadOnlyList<DryRunDeletion> Deletions { get; init; }

    /// <summary>Planned source disposition per matched file.</summary>
    public required IReadOnlyList<DryRunDisposition> Dispositions { get; init; }

    /// <summary>Total candidate files enumerated (matches + screened-out).</summary>
    public int CandidateCount => Matches.Count + ScreenedOut.Count;

    /// <summary>Number of Target writes that would overwrite an existing file.</summary>
    public int OverwriteCount => TargetWrites.Count(w => w.Action == TargetAction.Overwritten);

    /// <summary>Number of planned Mirror deletions.</summary>
    public int DeletionCount => Deletions.Count;
}
