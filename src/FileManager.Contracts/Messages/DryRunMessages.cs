namespace FileManager.Contracts.Messages;

/// <summary>
/// A request to preview (without acting) what processing a path would do (spec §2 dry-run). SHAPE ONLY
/// for M6: the engine that fills a meaningful report is M7, so the M6 service returns a
/// not-implemented/empty <see cref="DryRunReport"/> clearly marked via
/// <see cref="DryRunReport.Implemented"/>.
/// </summary>
/// <param name="Path">The absolute path that would be processed.</param>
/// <param name="ProfileId">Optional Profile id to preview under (null = resolve by Source).</param>
/// <param name="Recursive">Whether a directory path would be enumerated recursively.</param>
public sealed record DryRunRequest(string Path, string? ProfileId, bool Recursive);

/// <summary>One planned action a dry-run would take (shape only for M6).</summary>
/// <param name="SourcePath">The source file that would be processed.</param>
/// <param name="Action">The planned action description (e.g. <c>Copy</c>, <c>Skip</c>).</param>
/// <param name="Detail">Any extra human-readable detail.</param>
public sealed record DryRunItem(string SourcePath, string Action, string Detail);

/// <summary>
/// The result of a <see cref="DryRunRequest"/> (spec §2 dry-run). For M6 this is a shape-only stub:
/// <see cref="Implemented"/> is false and <see cref="Items"/> is empty, with <see cref="Note"/>
/// explaining that the dry-run engine ships in M7.
/// </summary>
/// <param name="Implemented">False in M6 (no preview engine yet); true once M7 fills the report.</param>
/// <param name="Items">The planned actions (empty in M6).</param>
/// <param name="Note">A human-readable note (e.g. the M6 not-implemented marker).</param>
public sealed record DryRunReport(bool Implemented, IReadOnlyList<DryRunItem> Items, string? Note)
{
    /// <summary>The M6 stub report: not implemented, no items, with the standard marker note.</summary>
    public static DryRunReport NotImplemented() =>
        new(false, Array.Empty<DryRunItem>(), "Dry-run preview is not implemented until M7.");
}
