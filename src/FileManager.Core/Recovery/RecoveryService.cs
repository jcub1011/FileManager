using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Safety;
using FileManager.Core.Transformers;

namespace FileManager.Core.Recovery;

/// <summary>
/// Startup crash recovery (§6.3). Scans the journal for Jobs left OPEN (never CLOSED) and brings each
/// to a safe terminal state, then closes its journal entry:
/// <list type="bullet">
/// <item><b>Pre-placement</b> (nothing staged/written/placed): the per-Job transformer workspace is
/// cleaned and the source is left in place for the watcher/scheduler to re-detect — no copies exist,
/// so there is nothing to undo and nothing to dispose.</item>
/// <item><b>Mid-placement</b> (a temp/final/staged artifact was recorded): the recorded artifacts are
/// rebuilt into a <see cref="RollbackContext"/> and undone via the <see cref="RollbackEngine"/>
/// (deleting this Job's finals/temps and restoring staged originals).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Why roll back rather than complete?</b> The journal records the artifact <i>locations</i>
/// but not the verification state of every pending Target, and the engine's atomic-rename completion
/// path runs inside <see cref="JobEngine"/> with live <c>IVerifier</c>/conflict context that recovery
/// does not have. Rolling a mid-placement Job back is the simpler, provably-safe default: it returns
/// the system to the pre-Job state (source intact, prior Target versions restored), after which the
/// normal trigger re-runs the Job cleanly. Completing remaining renames is a possible future
/// optimization, not a safety requirement.</para>
/// <para><b>The invariant.</b> A source is never disposed by recovery. The only path that disposes a
/// source is <see cref="JobEngine"/> after it journals <see cref="JournalEventType.AllTargetsVerified"/>;
/// any open Job that reached that point already placed every copy, so rolling it back (or leaving it)
/// can never produce a disposed source with missing copies — the §12 acceptance criterion.</para>
/// </remarks>
public sealed class RecoveryService
{
    private readonly IJournal _journal;
    private readonly RollbackEngine _rollbackEngine;
    private readonly IFileOperations _files;
    private readonly string _pipelineTempRoot;

    /// <summary>
    /// Creates a recovery service. <paramref name="options"/> resolves the pipeline temp root (and is
    /// the same options the engine uses), so a pre-placement Job's <c>&lt;pipelineTempRoot&gt;/&lt;jobId&gt;</c>
    /// workspace can be cleaned.
    /// </summary>
    public RecoveryService(
        IJournal journal,
        RollbackEngine rollbackEngine,
        IFileOperations files,
        JobEngineOptions? options = null)
    {
        _journal = journal;
        _rollbackEngine = rollbackEngine;
        _files = files;
        _pipelineTempRoot = (options ?? new JobEngineOptions()).ResolvePipelineTempRoot();
    }

    /// <summary>
    /// Runs one recovery pass over every open Job. Never throws on a single bad entry — a failure is
    /// captured in the returned <see cref="RecoveryReport"/> as a <see cref="RecoveryAction.Errored"/>
    /// row.
    /// </summary>
    public RecoveryReport Recover()
    {
        var results = new List<RecoveredJob>();

        foreach (OpenJobState job in _journal.ReadOpenEntries())
        {
            try
            {
                results.Add(RecoverOne(job));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new RecoveredJob(job.JobId, RecoveryAction.Errored, ex.Message));
                TryClose(job.JobId);
            }
        }

        return new RecoveryReport(results);
    }

    private RecoveredJob RecoverOne(OpenJobState job)
    {
        if (!job.InPlacement)
        {
            // Pre-placement: no copies exist. Clean only this Job's transformer workspace; the source
            // is untouched and will be re-detected by the trigger that originally found it.
            CleanWorkspace(job.JobId);
            _journal.Close(job.JobId);
            return new RecoveredJob(job.JobId, RecoveryAction.Cleaned, "pre-placement workspace cleaned");
        }

        // Mid-placement: undo everything this Job did (finals, temps, staged originals). The source is
        // never touched — recovery only ever rolls back, never disposes.
        RollbackContext ctx = job.ToRollbackContext();
        RollbackOutcome undo = _rollbackEngine.Rollback(ctx);
        CleanWorkspace(job.JobId);
        _journal.Close(job.JobId);

        string detail =
            $"rolled back: removed {undo.TempsRemoved} temp(s), {undo.FinalsRemoved} placed file(s), restored {undo.OriginalsRestored} original(s)";
        if (!undo.Clean)
            detail += $"; with errors: {string.Join("; ", undo.Errors)}";

        return new RecoveredJob(job.JobId, RecoveryAction.RolledBack, detail);
    }

    // Removes the per-Job transformer workspace (<pipelineTempRoot>/<PipelineDirName>/<jobId>),
    // best-effort. A leftover workspace is harmless, but cleaning it keeps the temp root tidy.
    private void CleanWorkspace(string jobId)
    {
        string workspace = Path.Combine(_pipelineTempRoot, TempWorkspace.PipelineDirName, jobId);
        try
        {
            _files.DeleteDirectory(workspace, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort; a residual workspace is reclaimed by a later sweep.
        }
    }

    private void TryClose(string jobId)
    {
        try
        {
            _journal.Close(jobId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
