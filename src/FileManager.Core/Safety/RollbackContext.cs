namespace FileManager.Core.Safety;

/// <summary>
/// Accumulates, as a Job distributes a file across Targets, exactly what the
/// <see cref="RollbackEngine"/> would need to undo on failure (§3.3): un-promoted temp artifacts,
/// finals this Job freshly placed, and prior Target versions moved into staging. Mutable and
/// single-Job scoped; never references the source file (which rollback must leave untouched).
/// </summary>
/// <remarks>
/// <b>Thread-safe (M5).</b> A Job's per-Target writes may run in parallel under the worker pool
/// (§5.4), so several threads can call the <c>Record*</c> mutators on the same context concurrently.
/// Every mutation and snapshot is guarded by a single lock. The <c>IReadOnlyList</c> properties return
/// a point-in-time <em>copy</em> so a reader (e.g. the rollback engine, after all Target tasks have
/// joined) iterates a stable snapshot that cannot be mutated underneath it.
/// </remarks>
public sealed class RollbackContext
{
    private readonly List<string> _unpromotedTemps = new();
    private readonly List<string> _placedFinals = new();
    private readonly List<StagedOriginal> _stagedOriginals = new();
    private readonly Lock _gate = new();

    /// <summary>A prior Target version moved aside under <c>StageOverwrites</c> and where to restore it.</summary>
    public sealed record StagedOriginal(string StagedPath, string OriginalPath);

    /// <summary>Temp artifacts written but not yet renamed into place (deleted on rollback).</summary>
    public IReadOnlyList<string> UnpromotedTemps
    {
        get { lock (_gate) return _unpromotedTemps.ToList(); }
    }

    /// <summary>Finals this Job placed (deleted on rollback so a Target keeps no half-finished set).</summary>
    public IReadOnlyList<string> PlacedFinals
    {
        get { lock (_gate) return _placedFinals.ToList(); }
    }

    /// <summary>Prior versions moved to staging (restored to their original paths on rollback).</summary>
    public IReadOnlyList<StagedOriginal> StagedOriginals
    {
        get { lock (_gate) return _stagedOriginals.ToList(); }
    }

    /// <summary>Records a temp that has been written but not yet promoted.</summary>
    public void RecordTemp(string tempPath)
    {
        lock (_gate) _unpromotedTemps.Add(tempPath);
    }

    /// <summary>
    /// Marks a recorded temp as promoted: it is no longer an orphan to delete, but the
    /// <paramref name="finalPath"/> it became <b>is</b> a fresh placement to remove on rollback.
    /// </summary>
    public void RecordPromotion(string tempPath, string finalPath)
    {
        lock (_gate)
        {
            _unpromotedTemps.Remove(tempPath);
            _placedFinals.Add(finalPath);
        }
    }

    /// <summary>
    /// Records a final this Job placed, without an originating temp to clear. Used when reconstructing
    /// a context from the durable journal (M4 recovery), where the temp→final promotion already
    /// happened on the crashed run and only the placed final needs undoing.
    /// </summary>
    public void RecordPlacedFinal(string finalPath)
    {
        lock (_gate) _placedFinals.Add(finalPath);
    }

    /// <summary>Records that <paramref name="originalPath"/>'s prior version was staged at <paramref name="stagedPath"/>.</summary>
    public void RecordStaged(string stagedPath, string originalPath)
    {
        lock (_gate) _stagedOriginals.Add(new StagedOriginal(stagedPath, originalPath));
    }
}
