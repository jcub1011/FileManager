using System.IO;
using FileManager.Core.IO;

namespace FileManager.Core.Transformers;

/// <summary>
/// The isolated per-Job scratch directory for the transformer chain
/// (<c>&lt;pipeline temp root&gt;/.pipeline_tmp/&lt;JobId&gt;/</c>, spec §4 Phase 3). The chain's working
/// copy and every intermediate live here; the original Source is never touched. Disposing tears the
/// whole subtree down best-effort — the engine keeps it alive until after distribution has read the
/// final working file, then disposes it in a <c>finally</c>.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    /// <summary>The directory segment that namespaces all pipeline workspaces under the temp root.</summary>
    public const string PipelineDirName = ".pipeline_tmp";

    private readonly IFileOperations _files;

    /// <summary>Absolute path of this Job's workspace root.</summary>
    public string Root { get; }

    private TempWorkspace(IFileOperations files, string root)
    {
        _files = files;
        Root = root;
    }

    /// <summary>Allocates (and creates on disk) the workspace for <paramref name="jobId"/>.</summary>
    public static TempWorkspace Create(IFileOperations files, string pipelineTempRoot, string jobId)
    {
        string root = Path.Combine(pipelineTempRoot, PipelineDirName, jobId);
        files.CreateDirectory(root);
        return new TempWorkspace(files, root);
    }

    /// <summary>An absolute path under the workspace root (creates nothing).</summary>
    public string PathFor(params string[] segments) =>
        Path.Combine(new[] { Root }.Concat(segments).ToArray());

    public void Dispose()
    {
        try
        {
            _files.DeleteDirectory(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort teardown; a leftover workspace is harmless and reclaimed on the next sweep.
        }
    }
}
