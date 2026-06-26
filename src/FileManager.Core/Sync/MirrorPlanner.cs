using System.IO;
using FileManager.Core.Audit;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using FileManager.Core.Routing;
using FileManager.Core.Trash;

namespace FileManager.Core.Sync;

/// <summary>A single surplus Target file that Mirror would remove, and where it currently lives.</summary>
public sealed record MirrorSurplus(string TargetRoot, string FilePath, string RelativeKey);

/// <summary>The set of surplus Target files a Mirror reconciliation would route to trash.</summary>
public sealed record MirrorPlan(IReadOnlyList<MirrorSurplus> Surplus);

/// <summary>The outcome of executing a <see cref="MirrorPlan"/>: per-file trash results.</summary>
public sealed record MirrorExecution(IReadOnlyList<MirrorDeletion> Deletions)
{
    /// <summary>Whether every surplus file was soft-deleted successfully.</summary>
    public bool AllSucceeded => Deletions.All(d => d.Result.Ok);
}

/// <summary>The result of routing one surplus file to trash.</summary>
public sealed record MirrorDeletion(MirrorSurplus Surplus, TrashResult Result);

/// <summary>
/// Computes and applies the <see cref="SyncMode.Mirror"/> reconciliation (§3.1.1): a Target becomes an
/// exact replica of the aggregated Source set, so files present at a Target but absent from any Source
/// are <b>surplus</b> and routed to the native trash (never hard-deleted). The relative-path key
/// honors the Profile's effective layout (the M:1 flatten-forcing rule from §3.1.2), matching how
/// distribution lands files.
/// </summary>
/// <remarks>
/// A standalone service: the M5 scheduler invokes it <b>after</b> placement under
/// <see cref="SyncMode.Mirror"/>. It is deliberately not part of the per-file
/// <c>JobEngine.ProcessFile</c> path, which reasons about a single file.
/// </remarks>
public sealed class MirrorPlanner
{
    private readonly IFileOperations files;
    private readonly ITrashService trash;
    private readonly IAuditLog _audit;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Creates a planner. <paramref name="audit"/> records an <see cref="AuditEntry"/> for every
    /// surplus deletion (§7); the default <see cref="NullAuditLog"/> keeps pre-M4 call sites working.
    /// <paramref name="clock"/> stamps audit timestamps (defaults to wall-clock UTC).
    /// </summary>
    public MirrorPlanner(
        IFileOperations files,
        ITrashService trash,
        IAuditLog? audit = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.files = files;
        this.trash = trash;
        _audit = audit ?? NullAuditLog.Instance;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Identifies surplus Target files: those whose layout-relative key is not produced by any Source
    /// file under the Profile's effective layout.
    /// </summary>
    public MirrorPlan Plan(Profile profile)
    {
        TargetLayout layout = TargetResolver.ResolveLayout(profile);
        HashSet<string> sourceKeys = BuildSourceKeySet(profile, layout);

        var surplus = new List<MirrorSurplus>();
        foreach (TargetSpec target in profile.Targets)
        {
            string root = PathNormalizer.Normalize(target.Path);
            foreach (string file in files.EnumerateFiles(root, recursive: true))
            {
                // Skip in-flight atomic-write temps so an orphaned temp is never "deleted" as surplus.
                if (file.EndsWith(AtomicFileWriter.TempSuffix, PathNormalizer.Comparison))
                    continue;

                string key = KeyForTargetFile(root, file, layout);
                if (!sourceKeys.Contains(key))
                    surplus.Add(new MirrorSurplus(target.Path, file, key));
            }
        }

        return new MirrorPlan(surplus);
    }

    /// <summary>
    /// Routes every file in <paramref name="plan"/> to the native trash, collecting results and
    /// recording a <see cref="AuditEntry"/> for each deletion that <b>actually occurred</b> (§7) —
    /// a failed <see cref="ITrashService.MoveToTrash"/> leaves the file in place, so it is not audited
    /// as a deletion. <paramref name="auditId"/> identifies the reconciliation in the audit trail (the
    /// Profile ID, since a Mirror sweep is not a single-file Job).
    /// </summary>
    public MirrorExecution Execute(MirrorPlan plan, string auditId)
    {
        var deletions = new List<MirrorDeletion>(plan.Surplus.Count);
        foreach (MirrorSurplus item in plan.Surplus)
        {
            TrashResult result = trash.MoveToTrash(item.FilePath);
            if (result.Ok)
            {
                _audit.Record(new AuditEntry
                {
                    Path = item.FilePath,
                    Action = AuditAction.MirrorDeletion,
                    Destination = result.TrashedPath,
                    Timestamp = _clock(),
                    JobId = auditId,
                });
            }

            deletions.Add(new MirrorDeletion(item, result));
        }

        return new MirrorExecution(deletions);
    }

    /// <summary>Plans then executes in one call (the scheduler's post-placement step).</summary>
    public MirrorExecution Reconcile(Profile profile) => Execute(Plan(profile), profile.ProfileId);

    private HashSet<string> BuildSourceKeySet(Profile profile, TargetLayout layout)
    {
        var keys = new HashSet<string>(KeyComparer(layout));
        foreach (SourceSpec source in profile.Sources)
        {
            string root = PathNormalizer.Normalize(source.Path);
            foreach (string file in files.EnumerateFiles(root, recursive: true))
            {
                // Skip in-flight atomic-write temps so an orphaned temp under a Source root (e.g. when
                // the pipeline temp root nests inside a Source) is never mistaken for a real Source file
                // that would suppress a legitimate Target surplus.
                if (file.EndsWith(AtomicFileWriter.TempSuffix, PathNormalizer.Comparison))
                    continue;

                string key = layout == TargetLayout.Flatten
                    ? Path.GetFileName(file)
                    : PathNormalizer.GetRelativePath(root, file);
                keys.Add(key);
            }
        }

        return keys;
    }

    private static string KeyForTargetFile(string targetRoot, string file, TargetLayout layout) =>
        layout == TargetLayout.Flatten
            ? Path.GetFileName(file)
            : PathNormalizer.GetRelativePath(targetRoot, file);

    private static StringComparer KeyComparer(TargetLayout layout) =>
        PathNormalizer.Comparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
