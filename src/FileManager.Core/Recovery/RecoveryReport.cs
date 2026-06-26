namespace FileManager.Core.Recovery;

/// <summary>The outcome of one open Job's recovery, for the structured <see cref="RecoveryReport"/>.</summary>
public enum RecoveryAction
{
    /// <summary>A pre-placement Job: its temp workspace was cleaned and the source left for re-detection.</summary>
    Cleaned,

    /// <summary>A mid-placement Job: rolled back (finals removed, staged originals restored).</summary>
    RolledBack,

    /// <summary>Recovery of this Job hit an error (recorded, not thrown).</summary>
    Errored,
}

/// <summary>One Job's recovery result.</summary>
public sealed record RecoveredJob(string JobId, RecoveryAction Action, string? Detail);

/// <summary>
/// A structured summary of a startup <see cref="RecoveryService.Recover"/> pass: per-Job outcomes plus
/// rolled-up counts. Recovery never throws on a single bad entry, so the report can mix cleaned,
/// rolled-back, and errored Jobs.
/// </summary>
public sealed record RecoveryReport(IReadOnlyList<RecoveredJob> Jobs)
{
    /// <summary>Number of pre-placement Jobs whose workspace was cleaned.</summary>
    public int Cleaned => Jobs.Count(j => j.Action == RecoveryAction.Cleaned);

    /// <summary>Number of mid-placement Jobs rolled back.</summary>
    public int RolledBack => Jobs.Count(j => j.Action == RecoveryAction.RolledBack);

    /// <summary>Number of Jobs whose recovery hit an error.</summary>
    public int Errors => Jobs.Count(j => j.Action == RecoveryAction.Errored);
}
