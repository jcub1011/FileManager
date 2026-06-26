using FileManager.Core.Profiles;
using FileManager.Core.Safety;

namespace FileManager.Core.Journal;

/// <summary>
/// The reconstructed state of a Job that the journal recorded as OPEN but never CLOSED — everything a
/// <see cref="FileManager.Core.Recovery.RecoveryService"/> needs to classify and finish or undo it.
/// Built by <see cref="IJournal.ReadOpenEntries"/> by folding all of a Job's records: the furthest
/// <see cref="LastEvent"/> reached plus the accumulated artifacts (mirroring
/// <see cref="RollbackContext"/>).
/// </summary>
public sealed record OpenJobState
{
    /// <summary>The Job's identifier.</summary>
    public required string JobId { get; init; }

    /// <summary>The Profile that drove the Job.</summary>
    public required string ProfileId { get; init; }

    /// <summary>The original source file path.</summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// The furthest transition recorded for this Job. Diagnostic only — recovery branches on
    /// <see cref="InPlacement"/> and <see cref="AllTargetsVerified"/>, not on this value.
    /// </summary>
    public required JournalEventType LastEvent { get; init; }

    /// <summary>The chosen source-disposition policy (mirrors the Profile's <c>OnSuccess</c>), if recorded.</summary>
    public OnSuccess? Disposition { get; init; }

    /// <summary>Where a recorded disposition landed the source (trash/archive), if any.</summary>
    public string? DispositionPath { get; init; }

    /// <summary>Whether <see cref="JournalEventType.AllTargetsVerified"/> was recorded — the disposition gate.</summary>
    public bool AllTargetsVerified { get; init; }

    /// <summary>Temp artifacts written but never observed promoted (delete on rollback).</summary>
    public IReadOnlyList<string> UnpromotedTemps { get; init; } = Array.Empty<string>();

    /// <summary>Finals this Job placed (delete on rollback).</summary>
    public IReadOnlyList<string> PlacedFinals { get; init; } = Array.Empty<string>();

    /// <summary>Prior Target versions moved to staging (restore on rollback).</summary>
    public IReadOnlyList<RollbackContext.StagedOriginal> StagedOriginals { get; init; } =
        Array.Empty<RollbackContext.StagedOriginal>();

    /// <summary>
    /// Whether the Job had begun placement: a prior version was staged, a temp was written, or a final
    /// was placed. Recovery treats a not-yet-in-placement Job as cleanable; a mid-placement Job is
    /// rolled back.
    /// </summary>
    public bool InPlacement =>
        StagedOriginals.Count > 0 || UnpromotedTemps.Count > 0 || PlacedFinals.Count > 0;

    /// <summary>Rebuilds a <see cref="RollbackContext"/> from the accumulated artifacts for rollback.</summary>
    public RollbackContext ToRollbackContext()
    {
        var ctx = new RollbackContext();
        foreach (string temp in UnpromotedTemps)
            ctx.RecordTemp(temp);
        foreach (string final in PlacedFinals)
            ctx.RecordPlacedFinal(final);
        foreach (RollbackContext.StagedOriginal staged in StagedOriginals)
            ctx.RecordStaged(staged.StagedPath, staged.OriginalPath);
        return ctx;
    }
}
