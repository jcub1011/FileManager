namespace FileManager.Core.Safety;

/// <summary>
/// Accumulates, as a Job distributes a file across Targets, exactly what the
/// <see cref="RollbackEngine"/> would need to undo on failure (§3.3): un-promoted temp artifacts,
/// finals this Job freshly placed, and prior Target versions moved into staging. Mutable and
/// single-Job scoped; never references the source file (which rollback must leave untouched).
/// </summary>
public sealed class RollbackContext
{
    private readonly List<string> _unpromotedTemps = new();
    private readonly List<string> _placedFinals = new();
    private readonly List<StagedOriginal> _stagedOriginals = new();

    /// <summary>A prior Target version moved aside under <c>StageOverwrites</c> and where to restore it.</summary>
    public sealed record StagedOriginal(string StagedPath, string OriginalPath);

    /// <summary>Temp artifacts written but not yet renamed into place (deleted on rollback).</summary>
    public IReadOnlyList<string> UnpromotedTemps => _unpromotedTemps;

    /// <summary>Finals this Job placed (deleted on rollback so a Target keeps no half-finished set).</summary>
    public IReadOnlyList<string> PlacedFinals => _placedFinals;

    /// <summary>Prior versions moved to staging (restored to their original paths on rollback).</summary>
    public IReadOnlyList<StagedOriginal> StagedOriginals => _stagedOriginals;

    /// <summary>Records a temp that has been written but not yet promoted.</summary>
    public void RecordTemp(string tempPath) => _unpromotedTemps.Add(tempPath);

    /// <summary>
    /// Marks a recorded temp as promoted: it is no longer an orphan to delete, but the
    /// <paramref name="finalPath"/> it became <b>is</b> a fresh placement to remove on rollback.
    /// </summary>
    public void RecordPromotion(string tempPath, string finalPath)
    {
        _unpromotedTemps.Remove(tempPath);
        _placedFinals.Add(finalPath);
    }

    /// <summary>
    /// Records a final this Job placed, without an originating temp to clear. Used when reconstructing
    /// a context from the durable journal (M4 recovery), where the temp→final promotion already
    /// happened on the crashed run and only the placed final needs undoing.
    /// </summary>
    public void RecordPlacedFinal(string finalPath) => _placedFinals.Add(finalPath);

    /// <summary>Records that <paramref name="originalPath"/>'s prior version was staged at <paramref name="stagedPath"/>.</summary>
    public void RecordStaged(string stagedPath, string originalPath) =>
        _stagedOriginals.Add(new StagedOriginal(stagedPath, originalPath));
}
