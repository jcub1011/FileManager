namespace FileManager.Core.Jobs;

/// <summary>
/// The lifecycle state of a single-file Job (§4). The happy path runs
/// <see cref="Open"/> → <see cref="Screened"/> → <see cref="Distributing"/> → <see cref="Placed"/>
/// → <see cref="Closed"/>; screening rejects end in <see cref="Skipped"/> and errors in
/// <see cref="Failed"/>.
/// </summary>
public enum JobState
{
    /// <summary>Ingested; journal OPEN (Phase 1).</summary>
    Open,

    /// <summary>Passed filter screening (Phase 2).</summary>
    Screened,

    /// <summary>Writing to Targets (Phase 4).</summary>
    Distributing,

    /// <summary>All Target writes placed (Phases 4–5).</summary>
    Placed,

    /// <summary>Source disposed; journal CLOSED (Phase 6).</summary>
    Closed,

    /// <summary>Screened out in Phase 2; ended gracefully.</summary>
    Skipped,

    /// <summary>Ended in error.</summary>
    Failed,
}
