using System.IO;
using FileManager.Core.Disposition;
using FileManager.Core.Filtering;
using FileManager.Core.IO;
using FileManager.Core.Logging;
using FileManager.Core.Profiles;
using FileManager.Core.Routing;

namespace FileManager.Core.Jobs;

/// <summary>
/// Runs the single-file Job lifecycle (§4) synchronously: ingest → screen → distribute → dispose.
/// Transformers (M2), real verification/rollback (M3), journaling (M4), and concurrency/triggers (M5)
/// are out of scope; <see cref="ProcessFile"/> is the programmatic entrypoint that later triggers call.
/// </summary>
public sealed class JobEngine
{
    private readonly IFileOperations _files;
    private readonly ILogSink _log;
    private readonly FilterEvaluator _evaluator;
    private readonly string _trashDirectory;

    public JobEngine(IFileOperations files, ILogSink log, JobEngineOptions? options = null)
    {
        _files = files;
        _log = log;
        _evaluator = new FilterEvaluator(files);
        _trashDirectory = (options ?? new JobEngineOptions()).ResolveTrashDirectory();
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

        // Phases 4–5 — Distribution to every Target (verification stubbed; atomic placement only).
        TargetLayout layout = TargetResolver.ResolveLayout(profile);
        var outcomes = new List<TargetOutcome>();
        try
        {
            foreach (TargetSpec target in profile.Targets)
            {
                string dest = TargetResolver.ResolveDestination(target, relativePath, fileName, layout);
                ConflictOutcome plan = ConflictResolver.Resolve(
                    _files, dest, meta, profile.Policies.ConflictResolution);

                if (plan.Action == TargetAction.Skipped)
                {
                    Emit(LogSeverity.Skip, "SKIPPED", $"{fileName} → {target.Path}: conflict policy skip");
                    outcomes.Add(new TargetOutcome(target.Path, null, TargetAction.Skipped));
                    continue;
                }

                AtomicFileWriter.Write(_files, source, plan.FinalPath!, plan.Overwrite);
                Emit(LogSeverity.Info, "PLACED", $"{fileName} → {plan.FinalPath} ({plan.Action})");
                outcomes.Add(new TargetOutcome(target.Path, plan.FinalPath, plan.Action));
            }
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            // Surface any Targets already written this run so a partial copy isn't silent.
            // Real rollback of those placements lands in M3 (§3.3).
            string placed = string.Join(", ", outcomes
                .Where(o => o.Action != TargetAction.Skipped)
                .Select(o => o.FinalPath ?? o.TargetRoot));
            if (placed.Length > 0)
                Emit(LogSeverity.Failure, "PARTIAL", $"{fileName}: distribution failed after placing: {placed}");
            return Failed($"Target distribution failed: {ex.Message}");
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
            disposition = SourceDisposer.Dispose(
                _files, source, profile.Policies, _trashDirectory, context.Now);
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
