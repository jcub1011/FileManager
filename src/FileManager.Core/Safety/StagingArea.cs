using System.IO;
using FileManager.Core.IO;

namespace FileManager.Core.Safety;

/// <summary>
/// The per-Job staging directory for <c>StageOverwrites</c> (§6.2). Immediately before a Target's
/// atomic rename, the prior version at that destination is moved here; on rollback it is restored to
/// its original path, and on success the whole area is discarded. Living off the destination is
/// acceptable because the move out happens only when a fresh temp is ready to take its place.
/// </summary>
/// <remarks>
/// Mirrors <see cref="FileManager.Core.Transformers.TempWorkspace"/>: a per-Job subtree under a
/// staging root, torn down best-effort on dispose. A leftover staging directory is harmless and
/// reclaimed by a later sweep.
/// </remarks>
public sealed class StagingArea : IDisposable
{
    /// <summary>The directory segment that namespaces all staging areas under the staging root.</summary>
    public const string StagingDirName = ".staging";

    private readonly IFileOperations _files;
    private int _next;

    /// <summary>Absolute path of this Job's staging root.</summary>
    public string Root { get; }

    private StagingArea(IFileOperations files, string root)
    {
        _files = files;
        Root = root;
    }

    /// <summary>Allocates (and creates on disk) the staging area for <paramref name="jobId"/>.</summary>
    public static StagingArea Create(IFileOperations files, string stagingRoot, string jobId)
    {
        string root = Path.Combine(stagingRoot, StagingDirName, jobId);
        files.CreateDirectory(root);
        return new StagingArea(files, root);
    }

    /// <summary>
    /// Moves the existing file at <paramref name="finalPath"/> into the staging area and returns its
    /// staged path. Each staged file gets a unique sub-name so multiple Targets sharing a base name
    /// never collide.
    /// </summary>
    public string Stage(string finalPath)
    {
        int slot = _next++;
        string staged = Path.Combine(Root, $"{slot}-{Path.GetFileName(finalPath)}");
        _files.Move(finalPath, staged, overwrite: false);
        return staged;
    }

    /// <summary>Moves a previously staged file back to <paramref name="originalPath"/> (overwriting).</summary>
    public void Restore(string stagedPath, string originalPath) =>
        _files.Move(stagedPath, originalPath, overwrite: true);

    /// <summary>Tears the staging area down, discarding all staged originals (called on success).</summary>
    public void DiscardAll() => Dispose();

    public void Dispose()
    {
        try
        {
            _files.DeleteDirectory(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort teardown; a leftover staging dir is harmless and reclaimed on the next sweep.
        }
    }
}
