using System.IO;
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
public sealed class MirrorPlanner(IFileOperations files, ITrashService trash)
{
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

    /// <summary>Routes every file in <paramref name="plan"/> to the native trash, collecting results.</summary>
    public MirrorExecution Execute(MirrorPlan plan)
    {
        var deletions = new List<MirrorDeletion>(plan.Surplus.Count);
        foreach (MirrorSurplus item in plan.Surplus)
            deletions.Add(new MirrorDeletion(item, trash.MoveToTrash(item.FilePath)));

        return new MirrorExecution(deletions);
    }

    /// <summary>Plans then executes in one call (the scheduler's post-placement step).</summary>
    public MirrorExecution Reconcile(Profile profile) => Execute(Plan(profile));

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
