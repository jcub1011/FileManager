using System.IO;
using FileManager.Core.Disposition;
using FileManager.Core.Filtering;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using FileManager.Core.Routing;
using FileManager.Core.Sync;
using FileManager.Core.Tokens;
using FileManager.Core.Transformers;

namespace FileManager.Core.Simulation;

/// <summary>
/// The side-effect-free run preview (spec §8). Given a <see cref="Profile"/> and a set of candidate
/// source files, it produces a <see cref="DryRunReport"/> by walking the EXACT same phase sequence the
/// live <see cref="FileManager.Core.Jobs.JobEngine"/> walks — Phase 2 filter screening, Phase 3
/// transformer command building, Phases 4–5 Target resolution + conflict planning, the Mirror
/// reconciliation, and Phase 6 source disposition — but in a strict <b>read-only mode</b>:
/// </summary>
/// <remarks>
/// <para>
/// <b>Zero filesystem mutations.</b> The engine only reads: it enumerates, probes existence, and reads
/// metadata. It NEVER writes, moves, renames, or deletes, and it NEVER launches a process. Concretely
/// it never calls <c>TransformerRunner.Run</c>, <c>MirrorPlanner.Execute</c>,
/// <c>SourceDisposer.Dispose</c>, or any <see cref="IFileOperations"/> write/move/delete member.
/// </para>
/// <para>
/// <b>No preview/reality drift.</b> Every decision is computed with the same shared functions the live
/// engine uses, never re-implemented here:
/// </para>
/// <list type="bullet">
/// <item>filter screening → <see cref="FilterEvaluator.ResolveEffective"/> + <see cref="IFilterEvaluator.Evaluate"/>;</item>
/// <item>transformer command → the same <see cref="TokenContext"/> +
///   <see cref="ArgumentParser.Parse"/> (Literal) / <see cref="ShellCommandBuilder.Build"/> (Shell) the
///   <c>TransformerRunner</c> feeds into its <c>ProcessLaunchSpec</c>;</item>
/// <item>Target placement → <see cref="TargetResolver.ResolveLayout"/> /
///   <see cref="TargetResolver.ResolveDestination"/> + <see cref="IConflictResolver.Resolve"/>;</item>
/// <item>Mirror surplus → <see cref="MirrorPlanner.Plan"/> (never <c>Execute</c>);</item>
/// <item>source disposition → <see cref="SourceDisposer.PreviewDisposition"/>, the pure decision shared
///   with the live disposer.</item>
/// </list>
/// <para>
/// The transformer command preview models the chain's <em>name/extension threading</em> the same way
/// <c>TransformerRunner</c> does (a NewFile step's output stem + <c>ExpectedOutputExtension</c> becomes
/// the next step's input), but it never stages a working copy or runs a step — so the previewed argv /
/// shell line is exactly what M2 would launch for the matching working file, with no process started.
/// </para>
/// </remarks>
public sealed class DryRunEngine
{
    private readonly IFileOperations _files;
    private readonly IFilterEvaluator _evaluator;
    private readonly IConflictResolver _conflictResolver;
    private readonly MirrorPlanner _mirrorPlanner;
    private readonly string _trashRoot;

    /// <summary>
    /// Full constructor wiring the read-only collaborators (tests inject fakes). The collaborators are
    /// the same TYPES the live engine uses, but the engine only ever invokes their read-only members.
    /// </summary>
    /// <param name="files">Read-only filesystem access (existence / metadata / enumerate).</param>
    /// <param name="evaluator">The live filter evaluator (Phase 2).</param>
    /// <param name="conflictResolver">The live conflict resolver (Phases 4–5; read-only).</param>
    /// <param name="mirrorPlanner">The live Mirror planner (<see cref="MirrorPlanner.Plan"/> only).</param>
    /// <param name="trashRoot">The local trash-fallback folder reported for a MoveToTrash disposition.</param>
    public DryRunEngine(
        IFileOperations files,
        IFilterEvaluator evaluator,
        IConflictResolver conflictResolver,
        MirrorPlanner mirrorPlanner,
        string trashRoot)
    {
        _files = files;
        _evaluator = evaluator;
        _conflictResolver = conflictResolver;
        _mirrorPlanner = mirrorPlanner;
        _trashRoot = trashRoot;
    }

    /// <summary>
    /// Convenience constructor building the standard read-only collaborators over
    /// <paramref name="files"/>. Mirrors the live engine's wiring (a <see cref="FilterEvaluator"/> over a
    /// <see cref="DedupeIndex"/>, a <see cref="ConflictResolver"/>, and a <see cref="MirrorPlanner"/>)
    /// but every one is exercised only through its read-only path here.
    /// </summary>
    public DryRunEngine(IFileOperations files, string trashRoot)
        : this(
            files,
            new FilterEvaluator(new DedupeIndex(files)),
            new ConflictResolver(files),
            // The planner's trash service is never invoked (we only call Plan), but it requires one;
            // pass a never-used no-op so we honor "executes nothing" even by construction.
            new MirrorPlanner(files, NoOpTrashService.Instance),
            trashRoot)
    {
    }

    /// <summary>
    /// Produces the full §8 preview for <paramref name="profile"/> over <paramref name="candidates"/>
    /// (absolute source-file paths, already enumerated read-only by the caller). Reads only; mutates
    /// nothing.
    /// </summary>
    /// <param name="profile">The Profile to preview under.</param>
    /// <param name="candidates">Absolute paths of the candidate source files to consider.</param>
    /// <param name="now">The clock anchor for age filters (matches the live engine's <c>context.Now</c>).</param>
    public DryRunReport Simulate(Profile profile, IReadOnlyList<string> candidates, DateTimeOffset now)
    {
        var matches = new List<DryRunMatch>();
        var screenedOut = new List<DryRunScreenedOut>();
        var commandPreviews = new List<DryRunCommandPreview>();
        var targetWrites = new List<DryRunTargetWrite>();
        var dispositions = new List<DryRunDisposition>();

        // Phase 4–5 layout is a per-Profile decision, resolved once (same as JobEngine).
        TargetLayout layout = TargetResolver.ResolveLayout(profile);

        // The disposition DECISION is per-Profile policy (shared pure function). Computing it once also
        // surfaces a misconfiguration (e.g. MoveToArchive without ArchiveFolder) the same way the live
        // engine would — but only for matched files, recorded per file below.
        DispositionDecision dispositionDecision = SourceDisposer.PreviewDisposition(profile.Policies, _trashRoot);

        foreach (string candidatePath in candidates)
        {
            string source = PathNormalizer.Normalize(candidatePath);

            // Phase 1 — Ingestion: resolve the owning Source and read metadata (read-only).
            SourceSpec? owningSource = ResolveSource(profile, source);
            if (owningSource is null)
            {
                // Not under any Source — the live engine fails the Job; in a preview we report it as a
                // screened-out file with a synthetic deciding "filter" so it is not silently dropped.
                screenedOut.Add(new DryRunScreenedOut(source, "NoOwningSource", null));
                continue;
            }

            string sourceRoot = PathNormalizer.Normalize(owningSource.Path);
            string fileName = Path.GetFileName(source);
            string relativePath = PathNormalizer.GetRelativePath(sourceRoot, source);

            FileMetadata meta;
            try
            {
                meta = _files.GetMetadata(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                screenedOut.Add(new DryRunScreenedOut(source, "Unreadable", ex.Message));
                continue;
            }

            // Phase 2 — Filter screening (the live ResolveEffective + Evaluate; the §8 deciding filter).
            FilterSet effective = FilterEvaluator.ResolveEffective(profile.Filters, owningSource.Filters);
            var candidate = new FilterCandidate
            {
                FileName = fileName,
                RelativePath = relativePath,
                Depth = ComputeDepth(relativePath),
                FullPath = source,
                Metadata = meta,
            };

            FilterDecision decision = _evaluator.Evaluate(effective, candidate, profile.Targets, now);
            if (!decision.Included)
            {
                screenedOut.Add(new DryRunScreenedOut(source, decision.DecidingFilter ?? "Excluded", decision.Detail));
                continue;
            }

            // Pass: the deciding filter for an included file is the sentinel "Pass" (mirrors FilterDecision.Pass).
            matches.Add(new DryRunMatch(source, relativePath, decision.DecidingFilter ?? "Pass"));

            // Phase 3 — Transformer command previews (no process launched). Threads the working-file
            // name/extension through the chain exactly as TransformerRunner does, building the SAME argv
            // (Literal) or shell command (Shell) for each step.
            string distFileName = fileName;
            string distRelativePath = relativePath;
            bool hasTransformers = profile.Transformers is { Count: > 0 };
            if (hasTransformers)
            {
                (distFileName, distRelativePath) = PreviewTransformerChain(
                    profile.Transformers!, source, sourceRoot, relativePath, fileName, commandPreviews);
            }

            // The metadata fed to OverwriteIfNewer. The working file does not exist (we ran nothing), so:
            //  - copy-only Profile: the placed file IS the source, so the source's mtime is exact;
            //  - transformer Profile: the live engine compares the PRODUCED file's mtime (≈ the run
            //    instant), so we stamp the stand-in metadata to `now` to match live behavior and avoid
            //    under-claiming an Overwrite as a Skip (FIX 4). All other fields (notably Length) keep
            //    the source values, which is the closest read-only approximation.
            FileMetadata distMeta = hasTransformers
                ? meta with { LastWriteTimeUtc = now.UtcDateTime }
                : meta;

            // Phases 4–5 — per-Target resolution + conflict planning (read-only Resolve). Track whether
            // ANY Target write is non-Skipped: the live JobEngine disposes the source ONLY when at least
            // one Target was actually written (anyWritten); when every Target is skipped (conflict policy)
            // it leaves the source in place. The preview must mirror that exactly (FIX 1) or it would
            // claim e.g. a PermanentDelete that reality never performs.
            bool anyWritten = false;
            foreach (TargetSpec target in profile.Targets)
            {
                string dest = TargetResolver.ResolveDestination(target, distRelativePath, distFileName, layout);
                ConflictOutcome plan = _conflictResolver.Resolve(dest, distMeta, profile.Policies.ConflictResolution);
                targetWrites.Add(new DryRunTargetWrite(source, target.Path, plan.FinalPath, plan.Action));
                if (plan.Action != TargetAction.Skipped)
                    anyWritten = true;
            }

            // Phase 6 — source disposition preview (the shared pure decision; never disposes). Mirroring
            // JobEngine: when nothing was written the source is left in place, so NO disposition is
            // recorded (it would otherwise misreport disposing an untouched source).
            if (anyWritten)
            {
                dispositions.Add(new DryRunDisposition(
                    source, dispositionDecision.Action, dispositionDecision.DestinationFolder));
            }
        }

        // Mirror reconciliation preview (§3.1.1): surplus Target files that would be routed to trash.
        // Uses MirrorPlanner.Plan ONLY (never Execute), and only under SyncMode.Mirror — matching the
        // scheduler's post-placement behavior.
        var deletions = new List<DryRunDeletion>();
        if (profile.SyncMode == SyncMode.Mirror)
        {
            MirrorPlan mirrorPlan = _mirrorPlanner.Plan(profile);
            foreach (MirrorSurplus surplus in mirrorPlan.Surplus)
                deletions.Add(new DryRunDeletion(surplus.TargetRoot, surplus.FilePath, surplus.RelativeKey));
        }

        return new DryRunReport
        {
            ProfileId = profile.ProfileId,
            Matches = matches,
            ScreenedOut = screenedOut,
            CommandPreviews = commandPreviews,
            TargetWrites = targetWrites,
            Deletions = deletions,
            Dispositions = dispositions,
        };
    }

    // Builds the per-step command previews for one file, returning the working file name + relative path
    // the chain's final output would have (so Phases 4–5 route the transformed name to the right place).
    // This mirrors TransformerRunner.Run's name/extension threading WITHOUT staging or executing anything.
    private (string FinalFileName, string FinalRelativePath) PreviewTransformerChain(
        IReadOnlyList<TransformerStep> steps,
        string sourcePath,
        string sourceRoot,
        string relativePath,
        string fileName,
        List<DryRunCommandPreview> previews)
    {
        // The runner stages a working copy named after the source inside the per-Job workspace, then
        // each step operates on it. We model the working-file NAME progression (which is all that affects
        // token expansion + final routing); the absolute working paths are workspace temp paths the
        // runner builds at execution time, so $step_input_path / $step_output_path are previewed against
        // a deterministic, clearly-marked synthetic workspace root rather than a real temp dir.
        string workspaceRoot = Path.Combine("<dry-run-workspace>", Path.GetFileNameWithoutExtension(fileName));
        string currentInputName = fileName;
        string currentInputPath = Path.Combine(workspaceRoot, currentInputName);

        foreach (TransformerStep step in steps.OrderBy(s => s.Step))
        {
            string? outputPath = null;
            if (step.OutputMode == OutputMode.NewFile && !string.IsNullOrWhiteSpace(step.ExpectedOutputExtension))
            {
                (string stem, _) = TokenExpander.SplitName(currentInputName);
                string outDir = Path.Combine(workspaceRoot, $"step{step.Step}");
                outputPath = Path.Combine(outDir, stem + step.ExpectedOutputExtension);
            }

            // The SAME context the runner builds: filename tokens reflect the CURRENT working file, with
            // the step input/output paths set (BuildContext in TransformerRunner).
            TokenContext context = TokenContext.ForFile(currentInputName, sourceRoot) with
            {
                StepInputPath = currentInputPath,
                StepOutputPath = outputPath,
            };

            bool literal = step.ArgumentMode == ArgumentMode.Literal;
            if (literal)
            {
                // EXACT live path: ArgumentParser.Parse produces the argv ProcessLaunchSpec would carry.
                IReadOnlyList<string> argv = ArgumentParser.Parse(step.Arguments, context);
                previews.Add(new DryRunCommandPreview(
                    sourcePath, step.Step, step.Name, step.ExecutablePath, Literal: true, argv));
            }
            else
            {
                // EXACT live path: ShellCommandBuilder.Build produces the single command string the
                // runner passes after the shell flag (its Arguments = { flag, command }).
                string command = ShellCommandBuilder.Build(step.ExecutablePath, step.Arguments, context);
                previews.Add(new DryRunCommandPreview(
                    sourcePath,
                    step.Step,
                    step.Name,
                    ShellCommandBuilder.ShellPath,
                    Literal: false,
                    new[] { ShellCommandBuilder.ShellCommandFlag, command }));
            }

            // A NewFile step's output becomes the next step's input (name + extension change carries).
            if (outputPath is not null)
            {
                currentInputName = Path.GetFileName(outputPath);
                currentInputPath = outputPath;
            }
            // InPlace: the same working file (name unchanged) carries forward.
        }

        // The final working file's name reroutes the relative path under PreserveStructure (ReplaceFileName).
        string finalRelativePath = ReplaceFileName(relativePath, currentInputName);
        return (currentInputName, finalRelativePath);
    }

    // --- Shared helpers mirroring JobEngine's private logic exactly (kept in sync deliberately) ---

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

    private static string ReplaceFileName(string relativePath, string newFileName)
    {
        string? dir = Path.GetDirectoryName(relativePath);
        return string.IsNullOrEmpty(dir) ? newFileName : Path.Combine(dir, newFileName);
    }

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
}
