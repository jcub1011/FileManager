using System.IO;
using FileManager.Core.Disposition;
using FileManager.Core.Filtering;
using FileManager.Core.IO;
using FileManager.Core.Logging;
using FileManager.Core.Metadata;
using FileManager.Core.Profiles;
using FileManager.Core.Routing;
using FileManager.Core.Safety;
using FileManager.Core.Transformers;
using FileManager.Core.Trash;
using FileManager.Core.Verification;

namespace FileManager.Core.Jobs;

/// <summary>
/// Runs the single-file Job lifecycle (§4) synchronously: ingest → screen → transform → distribute →
/// verify → dispose, with in-process rollback across all Targets on failure (§3.3). Durable
/// journaling (M4) and concurrency/triggers (M5) remain out of scope; <see cref="ProcessFile"/> is the
/// programmatic entrypoint that later triggers call.
/// </summary>
public sealed class JobEngine
{
    private readonly IFileOperations _files;
    private readonly ILogSink _log;
    private readonly IFilterEvaluator _evaluator;
    private readonly ITransformerRunner _transformerRunner;
    private readonly IConflictResolver _conflictResolver;
    private readonly ISourceDisposer _sourceDisposer;
    private readonly IVerifier? _verifier;
    private readonly MetadataCopier _metadataCopier;
    private readonly RollbackEngine _rollbackEngine;
    private readonly IFreeSpaceProbe _freeSpace;
    private readonly SpaceReservationLedger _ledger;
    private readonly string _trashDirectory;
    private readonly string _pipelineTempRoot;
    private readonly string _stagingRoot;

    /// <summary>
    /// Full constructor wiring the engine to its phase collaborators. Tests use this to inject fakes
    /// and exercise the orchestration in isolation; production code typically uses the convenience
    /// constructor below, which builds the standard implementations.
    /// </summary>
    /// <remarks>
    /// A non-null <paramref name="verifier"/> is used verbatim for every Job (tests inject a specific
    /// or always-fail verifier here). Pass null to have <see cref="ProcessFile"/> select the verifier
    /// per-Job from the Profile's <see cref="VerificationMethod"/> — the convenience constructor's
    /// behavior.
    /// </remarks>
    public JobEngine(
        IFileOperations files,
        ILogSink log,
        IFilterEvaluator evaluator,
        ITransformerRunner transformerRunner,
        IConflictResolver conflictResolver,
        ISourceDisposer sourceDisposer,
        IVerifier? verifier,
        MetadataCopier metadataCopier,
        RollbackEngine rollbackEngine,
        IFreeSpaceProbe freeSpace,
        SpaceReservationLedger ledger,
        JobEngineOptions? options = null)
    {
        _files = files;
        _log = log;
        _evaluator = evaluator;
        _transformerRunner = transformerRunner;
        _conflictResolver = conflictResolver;
        _sourceDisposer = sourceDisposer;
        _verifier = verifier;
        _metadataCopier = metadataCopier;
        _rollbackEngine = rollbackEngine;
        _freeSpace = freeSpace;
        _ledger = ledger;
        JobEngineOptions effective = options ?? new JobEngineOptions();
        _trashDirectory = effective.ResolveTrashDirectory();
        _pipelineTempRoot = effective.ResolvePipelineTempRoot();
        _stagingRoot = effective.ResolveStagingRoot();
    }

    /// <summary>
    /// Convenience constructor that builds the standard phase collaborators over
    /// <paramref name="files"/> (and <paramref name="processRunner"/>, defaulting to a real
    /// <see cref="SystemProcessRunner"/>). The verifier is selected per-Job from the Profile's
    /// <see cref="VerificationMethod"/> (null verifier ⇒ <see cref="ProcessFile"/> chooses), and the
    /// native platform trash is wired into source disposition.
    /// </summary>
    /// <remarks>
    /// Builds a <see cref="SystemFreeSpaceProbe"/> and a <b>per-engine</b>
    /// <see cref="SpaceReservationLedger"/> seeded with
    /// <see cref="JobEngineOptions.MinFreeSpaceMarginBytes"/>. A per-engine ledger is correct for
    /// today's single-threaded engine (every Job reserves then releases before the next runs). M5/M6
    /// will instead inject one <b>shared</b> ledger (via the full constructor) across the worker pool,
    /// so concurrent Jobs writing to the same volume see each other's outstanding reservations.
    /// </remarks>
    public JobEngine(IFileOperations files, ILogSink log, JobEngineOptions? options = null, IProcessRunner? processRunner = null)
        : this(files, log, processRunner, options ?? new JobEngineOptions())
    {
    }

    // Resolves the standard collaborators against a single, already-resolved JobEngineOptions instance
    // (so the defaults aren't reallocated and every collaborator sees the same options).
    private JobEngine(IFileOperations files, ILogSink log, IProcessRunner? processRunner, JobEngineOptions effective)
        : this(
            files,
            log,
            new FilterEvaluator(new DedupeIndex(files)),
            new TransformerRunner(files, processRunner ?? new SystemProcessRunner()),
            new ConflictResolver(files),
            new SourceDisposer(files, TrashServiceFactory.Create(
                files,
                effective.ResolveTrashDirectory(),
                freeSpace: null,
                effective.MinFreeSpaceMarginBytes)),
            verifier: null,
            new MetadataCopier(files),
            new RollbackEngine(files),
            new SystemFreeSpaceProbe(),
            new SpaceReservationLedger(new SystemFreeSpaceProbe(), effective.MinFreeSpaceMarginBytes),
            effective)
    {
    }

    /// <summary>
    /// Processes one file under <paramref name="profile"/> end to end and returns a
    /// <see cref="JobResult"/>. Never throws for ordinary I/O failures — those resolve to a
    /// <see cref="JobState.Failed"/> result with a reason.
    /// </summary>
    public JobResult ProcessFile(Profile profile, string sourcePath, IngestionContext context)
    {
        string jobId = Guid.NewGuid().ToString("N");
        string source = PathNormalizer.Normalize(sourcePath);
        string fileName = Path.GetFileName(source);
        var logs = new List<JobLogEntry>();

        void Emit(LogSeverity severity, string code, string message)
        {
            var entry = new JobLogEntry { Severity = severity, Code = code, JobId = jobId, Message = message };
            logs.Add(entry);
            if (VerbosityFilter.ShouldEmit(profile.Logging.Verbosity, severity))
                _log.Log(entry);
        }

        JobResult Failed(string reason)
        {
            Emit(LogSeverity.Failure, "FAILED", $"{fileName}: {reason}");
            return new JobResult
            {
                JobId = jobId,
                State = JobState.Failed,
                SourcePath = source,
                FailureReason = reason,
                Logs = logs,
            };
        }

        void EmitStep(StepResult step)
        {
            string shellTag = step.Shell ? " [shell]" : string.Empty;
            if (step.Succeeded)
                Emit(LogSeverity.Info, "STEP", $"Step {step.Step} ({step.Name}){shellTag}: exit {step.ExitCode}");
            else if (step.TimedOut)
                Emit(LogSeverity.Failure, "STEP_TIMEOUT", $"Step {step.Step} ({step.Name}){shellTag} timed out");
            else
                Emit(LogSeverity.Failure, "STEP_FAILED", $"Step {step.Step} ({step.Name}){shellTag}: exit {step.ExitCode}");

            // stdout/stderr always land in the JobResult log (the sink applies verbosity); failure
            // diagnostics are not lost even at low verbosity because they ride the entries above.
            if (step.StandardOutput.Length > 0)
                Emit(LogSeverity.Info, "STEP_STDOUT", $"Step {step.Step} stdout: {step.StandardOutput}");
            if (step.StandardError.Length > 0)
                Emit(LogSeverity.Info, "STEP_STDERR", $"Step {step.Step} stderr: {step.StandardError}");
        }

        // Phase 1 — Ingestion: resolve the owning Source and snapshot metadata.
        SourceSpec? owningSource = ResolveSource(profile, source);
        if (owningSource is null)
            return Failed("Source path is not under any configured Source.");

        string sourceRoot = PathNormalizer.Normalize(owningSource.Path);

        FileMetadata meta;
        try
        {
            meta = _files.GetMetadata(source);
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            return Failed($"Could not read source metadata: {ex.Message}");
        }

        // Phase 2 — Filter screening.
        FilterSet effective = FilterEvaluator.ResolveEffective(profile.Filters, owningSource.Filters);
        string relativePath = PathNormalizer.GetRelativePath(sourceRoot, source);
        var candidate = new FilterCandidate
        {
            FileName = fileName,
            RelativePath = relativePath,
            Depth = ComputeDepth(relativePath),
            FullPath = source,
            Metadata = meta,
        };

        FilterDecision decision;
        try
        {
            decision = _evaluator.Evaluate(effective, candidate, profile.Targets, context.Now);
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            return Failed($"Filter evaluation failed: {ex.Message}");
        }

        if (!decision.Included)
        {
            Emit(LogSeverity.Skip, "SKIPPED", $"{fileName}: rejected by {decision.DecidingFilter}");
            return new JobResult
            {
                JobId = jobId,
                State = JobState.Skipped,
                SourcePath = source,
                DecidingFilter = decision.DecidingFilter,
                Logs = logs,
            };
        }

        // The file that Targets actually receive. Without a transformer chain this is the original
        // source; with one it becomes the chain's final working file (possibly renamed/re-extensioned).
        string distSource = source;
        string distFileName = fileName;
        string distRelativePath = relativePath;
        FileMetadata distMeta = meta;

        // The transformer workspace must outlive the chain so distribution can read the working file;
        // it is torn down in the finally only after Phases 4–5 have copied it out. The staging area
        // (StageOverwrites) and rollback context likewise span the whole distribution.
        var outcomes = new List<TargetOutcome>();
        TempWorkspace? workspace = null;
        StagingArea? staging = null;
        SpaceReservation? reservation = null;
        var rollback = new RollbackContext();

        // Set when a rollback cannot restore every staged prior version: those originals are still
        // sitting in the staging area, so the finally teardown must NOT delete it (that would destroy
        // the user's last copy of the prior Target file). M4's journal makes this resumable.
        bool preserveStaging = false;

        // The verifier is the injected one (tests) or, for production, selected from the Profile.
        IVerifier verifier = _verifier
            ?? VerifierFactory.Create(profile.Policies.VerificationMethod, _files);
        bool stageOverwrites = profile.Policies.OverwriteHandling == OverwriteHandling.StageOverwrites;

        try
        {
            // Phase 3 — Transformer chain (only when the Profile defines one). Runs on an isolated
            // working copy, so the original source is never mutated; a step failure/timeout aborts.
            if (profile.Transformers is { Count: > 0 } transformers)
            {
                TransformerChainResult chain;
                try
                {
                    workspace = TempWorkspace.Create(_files, _pipelineTempRoot, jobId);
                    chain = _transformerRunner.Run(workspace, transformers, source, sourceRoot);
                }
                catch (Exception ex) when (IsIoError(ex))
                {
                    return Failed($"Transformer chain failed: {ex.Message}");
                }

                foreach (StepResult step in chain.Steps)
                    EmitStep(step);

                if (!chain.Succeeded)
                    return Failed(chain.FailureReason ?? "Transformer chain aborted.");

                distSource = chain.FinalWorkingFile!;
                distFileName = Path.GetFileName(distSource);
                distRelativePath = ReplaceFileName(relativePath, distFileName);
                try
                {
                    distMeta = _files.GetMetadata(distSource);
                }
                catch (Exception ex) when (IsIoError(ex))
                {
                    return Failed($"Could not read transformed file metadata: {ex.Message}");
                }
            }

            // Phases 4–5 — Distribution + verification to every Target. Per Target the engine writes a
            // temp copy, verifies it against the final output, optionally stages the prior version,
            // then atomically promotes the temp and copies metadata. ANY failure (I/O, verification,
            // or a FailJob metadata loss) rolls back every Target for this file and leaves the source
            // untouched (§3.3). On success the staged originals are discarded.
            TargetLayout layout = TargetResolver.ResolveLayout(profile);

            // Pre-flight reservation (§3.3 proactive disk-space): reserve the bytes every Target write
            // (and any cross-volume staging move) will consume BEFORE writing a single byte, so a
            // doomed Job fails fast leaving the source untouched and the Targets clean. Runs here —
            // after Phase 3 so distMeta.Length reflects any transform, and before the distribution loop.
            // Transformer scratch space is deliberately NOT reserved: a transform's intermediate/output
            // sizes are unknowable up front, so the reactive rollback still covers a mid-transform ENOSPC.
            var spaceRequests = new List<SpaceRequest>();
            string stagingRootDir = _stagingRoot;
            string? stagingVolume = null; // resolved lazily only when a staging move would be cross-volume

            // Staging only ever fires when the conflict policy can replace an existing Target in place
            // (Overwrite / OverwriteIfNewer → plan.Overwrite). RenameSuffix/Skip route to a fresh name
            // and never stage, so gating here avoids reserving staging space the run will never use.
            bool policyCanOverwrite = profile.Policies.ConflictResolution
                is ConflictResolution.Overwrite or ConflictResolution.OverwriteIfNewer;
            foreach (TargetSpec target in profile.Targets)
            {
                string dest = TargetResolver.ResolveDestination(target, distRelativePath, distFileName, layout);
                // Probe the destination's directory (its volume) — the file itself does not exist yet.
                string destDir = Path.GetDirectoryName(dest) ?? target.Path;
                spaceRequests.Add(new SpaceRequest(destDir, distMeta.Length));

                // StageOverwrites moves an existing prior version aside before the rename. A same-volume
                // move is a rename (no extra space); only a cross-volume staging move copies bytes, so
                // reserve the prior file's size on the staging volume in that case.
                if (stageOverwrites && policyCanOverwrite && _files.FileExists(dest))
                {
                    string targetVolume = _freeSpace.Probe(destDir).VolumeKey;
                    stagingVolume ??= _freeSpace.Probe(stagingRootDir).VolumeKey;
                    if (!string.Equals(stagingVolume, targetVolume, PathNormalizer.Comparison))
                    {
                        long priorSize = _files.GetMetadata(dest).Length;
                        spaceRequests.Add(new SpaceRequest(stagingRootDir, priorSize));
                    }
                }
            }

            ReservationResult spaceResult = _ledger.TryReserve(spaceRequests);
            if (!spaceResult.Ok)
            {
                Emit(LogSeverity.Failure, "NO_SPACE", $"{distFileName}: insufficient space: {spaceResult.Reason}");
                return Failed($"Insufficient space: {spaceResult.Reason}");
            }

            reservation = spaceResult.Handle;

            // RollbackThenFail runs the §3.3 rollback, logs it, and returns the Failed result.
            JobResult RollbackThenFail(string reason)
            {
                RollbackOutcome undo = _rollbackEngine.Rollback(rollback);
                string detail =
                    $"removed {undo.TempsRemoved} temp(s), {undo.FinalsRemoved} placed file(s), restored {undo.OriginalsRestored} original(s)";
                if (!undo.Clean)
                    detail += $"; with errors: {string.Join("; ", undo.Errors)}";
                Emit(LogSeverity.Failure, "ROLLBACK", $"{distFileName}: {detail}");

                // Any staged prior version that rollback could not restore is still in the staging
                // area. Preserve it (suppress the finally teardown) and tell the operator where it is,
                // rather than silently destroying the user's last copy of that Target file.
                int unrestored = rollback.StagedOriginals.Count - undo.OriginalsRestored;
                if (unrestored > 0 && staging is not null)
                {
                    preserveStaging = true;
                    Emit(LogSeverity.Failure, "STAGING_PRESERVED",
                        $"{distFileName}: {unrestored} prior Target version(s) could not be restored and remain in {staging.Root}");
                }

                return Failed(reason);
            }

            try
            {
                foreach (TargetSpec target in profile.Targets)
                {
                    string dest = TargetResolver.ResolveDestination(target, distRelativePath, distFileName, layout);
                    ConflictOutcome plan = _conflictResolver.Resolve(
                        dest, distMeta, profile.Policies.ConflictResolution);

                    if (plan.Action == TargetAction.Skipped)
                    {
                        Emit(LogSeverity.Skip, "SKIPPED", $"{distFileName} → {target.Path}: conflict policy skip");
                        outcomes.Add(new TargetOutcome(target.Path, null, TargetAction.Skipped));
                        continue;
                    }

                    string finalPath = plan.FinalPath!;

                    // Write the copy to a temp beside the destination and record it for rollback.
                    string temp = AtomicFileWriter.WriteTemp(_files, distSource, finalPath);
                    rollback.RecordTemp(temp);

                    // Verify the copy against the Job's final output BEFORE any rename/disposition.
                    VerificationResult verification = verifier.Verify(distSource, temp);
                    if (!verification.Ok)
                    {
                        Emit(LogSeverity.Failure, "VERIFY_FAILED",
                            $"{distFileName} → {finalPath}: {verification.Reason}");
                        return RollbackThenFail($"Verification failed for {finalPath}: {verification.Reason}");
                    }

                    Emit(LogSeverity.Info, "VERIFIED", $"{distFileName} → {finalPath}");

                    // Under StageOverwrites, move the prior version aside immediately before the rename
                    // so rollback can restore it byte-for-byte.
                    if (plan.Overwrite && stageOverwrites && _files.FileExists(finalPath))
                    {
                        staging ??= StagingArea.Create(_files, _stagingRoot, jobId);
                        string stagedPath = staging.Stage(finalPath);
                        rollback.RecordStaged(stagedPath, finalPath);
                        Emit(LogSeverity.Info, "STAGED", $"{finalPath} → staged prior version");
                    }

                    // Atomic rename into place; the temp is now a fresh final to remove on rollback.
                    AtomicFileWriter.Promote(_files, temp, finalPath, plan.Overwrite);
                    rollback.RecordPromotion(temp, finalPath);
                    Emit(LogSeverity.Info, "PLACED", $"{distFileName} → {finalPath} ({plan.Action})");

                    // Best-effort metadata preservation; a FailJob-detected loss rolls back.
                    MetadataResult metadata = _metadataCopier.Copy(
                        distSource, finalPath, profile.Policies.MetadataOnConflict);
                    if (!metadata.Ok)
                    {
                        Emit(LogSeverity.Failure, "METADATA_FAILED",
                            $"{distFileName} → {finalPath}: {metadata.Warning}");
                        return RollbackThenFail($"Metadata preservation failed for {finalPath}: {metadata.Warning}");
                    }

                    if (metadata.Warning is not null)
                        Emit(LogSeverity.Info, "METADATA_WARN", $"{distFileName} → {finalPath}: {metadata.Warning}");

                    outcomes.Add(new TargetOutcome(target.Path, finalPath, plan.Action));
                }
            }
            catch (Exception ex) when (IsIoError(ex))
            {
                return RollbackThenFail($"Target distribution failed: {ex.Message}");
            }

            // Success — the staged prior versions are no longer needed.
            staging?.DiscardAll();
        }
        finally
        {
            workspace?.Dispose();
            if (!preserveStaging)
                staging?.Dispose();
            // Release the pre-flight space reservation (idempotent) so the next Job sees the freed
            // bytes. Today's single-threaded engine reserves→releases per Job; a shared ledger (M5/M6)
            // makes this release visible to other workers.
            reservation?.Dispose();
        }

        // Phase 6 — Source disposition. Only dispose of the source when at least one Target was
        // actually written; if every Target was skipped (conflict policy), nothing was copied this
        // run, so the source must be left in place — disposing it (e.g. PermanentDelete) would be
        // data loss.
        bool anyWritten = outcomes.Any(o => o.Action != TargetAction.Skipped);
        if (!anyWritten)
        {
            Emit(LogSeverity.Skip, "DISPOSED", $"{fileName}: all targets skipped; source left in place");
            return new JobResult
            {
                JobId = jobId,
                State = JobState.Closed,
                SourcePath = source,
                Targets = outcomes,
                Disposition = null,
                Logs = logs,
            };
        }

        DispositionOutcome disposition;
        try
        {
            disposition = _sourceDisposer.Dispose(
                source, profile.Policies, _trashDirectory, context.Now);
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            return Failed($"Source disposition failed: {ex.Message}");
        }

        Emit(LogSeverity.Info, "DISPOSED", $"{fileName}: {disposition.Action}");

        return new JobResult
        {
            JobId = jobId,
            State = JobState.Closed,
            SourcePath = source,
            Targets = outcomes,
            Disposition = disposition.Action,
            DispositionPath = disposition.ResultPath,
            Logs = logs,
        };
    }

    /// <summary>
    /// The Source whose root contains <paramref name="source"/>, choosing the longest (most specific)
    /// match when roots nest. Returns null when no Source owns the path.
    /// </summary>
    private static SourceSpec? ResolveSource(Profile profile, string source)
    {
        SourceSpec? best = null;
        int bestRootLength = -1;

        foreach (SourceSpec candidate in profile.Sources)
        {
            if (!PathNormalizer.IsUnder(candidate.Path, source))
                continue;

            int rootLength = PathNormalizer.Normalize(candidate.Path).Length;
            if (rootLength > bestRootLength)
            {
                best = candidate;
                bestRootLength = rootLength;
            }
        }

        return best;
    }

    /// <summary>
    /// A Source-relative path with its file name swapped for <paramref name="newFileName"/>, keeping
    /// the original subfolder. Used so a transformer that renames/re-extensions the file still lands
    /// in the same relative location under each Target's <see cref="TargetLayout.PreserveStructure"/>.
    /// </summary>
    private static string ReplaceFileName(string relativePath, string newFileName)
    {
        string? dir = Path.GetDirectoryName(relativePath);
        return string.IsNullOrEmpty(dir) ? newFileName : Path.Combine(dir, newFileName);
    }

    /// <summary>Subfolder depth from a Source-relative path; 0 = directly in the Source root.</summary>
    private static int ComputeDepth(string relativePath)
    {
        int depth = 0;
        foreach (char c in relativePath)
        {
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
                depth++;
        }

        return depth;
    }

    // Only genuine I/O faults resolve to a Failed Job. ArgumentException/InvalidOperationException
    // signal programmer/config error (e.g. a malformed path, or MoveToArchive without ArchiveFolder —
    // the latter is caught at profile validation) and must surface rather than be masked as a
    // per-file I/O failure.
    private static bool IsIoError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;
}
