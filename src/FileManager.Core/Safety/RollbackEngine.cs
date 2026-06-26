using System.IO;
using FileManager.Core.IO;

namespace FileManager.Core.Safety;

/// <summary>A summary of what a rollback undid, for logging/surfacing (§3.3 step 5).</summary>
public sealed record RollbackOutcome(
    int TempsRemoved,
    int FinalsRemoved,
    int OriginalsRestored,
    IReadOnlyList<string> Errors)
{
    /// <summary>Whether the rollback completed every step without a best-effort failure.</summary>
    public bool Clean => Errors.Count == 0;
}

/// <summary>
/// Performs the §3.3 rollback for a single file across all its Targets: it (1) deletes un-promoted
/// temp artifacts, (2) deletes finals this Job freshly placed (so no Target is left with a
/// half-finished set), and (3) restores prior versions that <c>StageOverwrites</c> moved aside. It
/// <b>never</b> touches the source file. Every step is best-effort and tolerant of artifacts that are
/// already gone, so a partial failure mid-distribution still cleans up as much as possible; any
/// residual error is reported (not thrown) for logging.
/// </summary>
public sealed class RollbackEngine(IFileOperations files)
{
    /// <summary>Undoes everything recorded in <paramref name="context"/>.</summary>
    public RollbackOutcome Rollback(RollbackContext context)
    {
        var errors = new List<string>();
        int tempsRemoved = 0, finalsRemoved = 0, originalsRestored = 0;

        // 1. Remove un-promoted temp artifacts from all Targets.
        foreach (string temp in context.UnpromotedTemps)
        {
            if (TryDelete(temp, errors))
                tempsRemoved++;
        }

        // 2. Remove finals this Job placed (including already-completed Targets).
        foreach (string final in context.PlacedFinals)
        {
            if (TryDelete(final, errors))
                finalsRemoved++;
        }

        // 3. Restore staged prior versions over their original paths.
        foreach (RollbackContext.StagedOriginal staged in context.StagedOriginals)
        {
            try
            {
                if (files.FileExists(staged.StagedPath))
                {
                    files.Move(staged.StagedPath, staged.OriginalPath, overwrite: true);
                    originalsRestored++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"restore {staged.OriginalPath}: {ex.Message}");
            }
        }

        return new RollbackOutcome(tempsRemoved, finalsRemoved, originalsRestored, errors);
    }

    private bool TryDelete(string path, List<string> errors)
    {
        try
        {
            if (!files.FileExists(path))
                return false;
            files.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"delete {path}: {ex.Message}");
            return false;
        }
    }
}
