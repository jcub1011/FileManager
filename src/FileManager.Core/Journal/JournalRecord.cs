namespace FileManager.Core.Journal;

/// <summary>
/// One durable, framed transition in a Job's life (§4 / §6.3). Each record is self-describing: the Job
/// it belongs to, the event reached, and — for placement/staging events — the artifact paths recovery
/// needs to finish or undo the Job. The artifact fields mirror
/// <see cref="FileManager.Core.Safety.RollbackContext"/> exactly (un-promoted temp, placed final,
/// staged-original pair) so a recovery scan can rebuild a <c>RollbackContext</c> from the journal.
/// </summary>
/// <remarks>
/// A sealed record with <c>init</c>-only members so a written record is immutable. The
/// <see cref="SchemaVersion"/> is stamped on every record (not just OPEN) so a torn or foreign record
/// can be told apart from a current one. Serialized via the source generator
/// (<see cref="FileManager.Core.Profiles.ProfileJsonContext"/>); no reflection.
/// </remarks>
public sealed record JournalRecord
{
    /// <summary>The schema version this record was written under (current is <see cref="CurrentSchemaVersion"/>).</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Versioned schema marker, stamped on every record.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>The transition this record captures.</summary>
    public required JournalEventType Event { get; init; }

    /// <summary>The Job this record belongs to (correlates all of a Job's records).</summary>
    public required string JobId { get; init; }

    /// <summary>The Profile that drove the Job.</summary>
    public required string ProfileId { get; init; }

    /// <summary>The original source file path.</summary>
    public required string SourcePath { get; init; }

    /// <summary>When the transition occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The disposition policy chosen for the source (mirrors the Profile's <c>OnSuccess</c>).</summary>
    public Profiles.OnSuccess? Disposition { get; init; }

    /// <summary>
    /// An un-promoted temp artifact recorded by a <see cref="JournalEventType.TargetVerified"/> event
    /// (the temp written beside a Target but not yet renamed into place). Mirrors
    /// <see cref="FileManager.Core.Safety.RollbackContext.UnpromotedTemps"/>.
    /// </summary>
    public string? TempPath { get; init; }

    /// <summary>
    /// A final path this Job placed, recorded by a <see cref="JournalEventType.TargetPlaced"/> event.
    /// Mirrors <see cref="FileManager.Core.Safety.RollbackContext.PlacedFinals"/>.
    /// </summary>
    public string? FinalPath { get; init; }

    /// <summary>
    /// Where a prior Target version was moved, recorded by a <see cref="JournalEventType.TargetStaged"/>
    /// event. Paired with <see cref="StagedOriginalPath"/>; mirrors a
    /// <see cref="FileManager.Core.Safety.RollbackContext.StagedOriginal"/>.
    /// </summary>
    public string? StagedPath { get; init; }

    /// <summary>The original path a staged prior version must be restored to (paired with <see cref="StagedPath"/>).</summary>
    public string? StagedOriginalPath { get; init; }

    /// <summary>The destination a disposition landed the source at (trash/archive path), when applicable.</summary>
    public string? DispositionPath { get; init; }
}
