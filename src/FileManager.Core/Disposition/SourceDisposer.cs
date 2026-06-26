using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using FileManager.Core.Tokens;
using FileManager.Core.Trash;

namespace FileManager.Core.Disposition;

/// <summary>The result of disposing of a source file (Phase 6).</summary>
public sealed record DispositionOutcome(OnSuccess Action, string? ResultPath);

/// <summary>
/// Phase 6 source disposition (§4) per <see cref="OnSuccess"/>.
/// </summary>
/// <remarks>
/// <see cref="OnSuccess.MoveToTrash"/> delegates to an injected <see cref="ITrashService"/> (§5.3 —
/// the native Recycle Bin / FreeDesktop Trash). When none is supplied the disposer falls back to a
/// local <c>trash/</c> folder rooted at the <c>trashRoot</c> argument, matching the original M1
/// placeholder behavior so existing call sites stay source-compatible.
/// </remarks>
public sealed class SourceDisposer : ISourceDisposer
{
    private readonly IFileOperations files;
    private readonly ITrashService? _trash;

    /// <summary>
    /// Creates a disposer. When <paramref name="trash"/> is null, <see cref="OnSuccess.MoveToTrash"/>
    /// uses a local-folder fallback rooted at the <c>trashRoot</c> passed to <see cref="Dispose"/>.
    /// </summary>
    public SourceDisposer(IFileOperations files, ITrashService? trash = null)
    {
        this.files = files;
        _trash = trash;
    }

    /// <summary>
    /// Applies the disposition. <paramref name="trashRoot"/> is the placeholder trash folder used for
    /// <see cref="OnSuccess.MoveToTrash"/>; <paramref name="now"/> stamps the trashed name.
    /// </summary>
    public DispositionOutcome Dispose(
        string sourcePath,
        PolicySet policies,
        string trashRoot,
        DateTimeOffset now)
    {
        switch (policies.OnSuccess)
        {
            case OnSuccess.KeepSource:
                return new DispositionOutcome(OnSuccess.KeepSource, sourcePath);

            case OnSuccess.PermanentDelete:
                files.Delete(sourcePath);
                return new DispositionOutcome(OnSuccess.PermanentDelete, null);

            case OnSuccess.MoveToArchive:
                string archive = policies.ArchiveFolder
                    ?? throw new InvalidOperationException("MoveToArchive requires ArchiveFolder.");
                string archived = MoveInto(sourcePath, archive, prefix: null);
                return new DispositionOutcome(OnSuccess.MoveToArchive, archived);

            case OnSuccess.MoveToTrash:
                if (_trash is not null)
                {
                    TrashResult result = _trash.MoveToTrash(sourcePath);
                    if (!result.Ok)
                        throw new IOException($"MoveToTrash failed: {result.Reason}");
                    return new DispositionOutcome(OnSuccess.MoveToTrash, result.TrashedPath);
                }

                // Local-folder fallback (no native trash injected). Stamp to keep trashed names unique.
                string stamp = now.UtcDateTime.ToString("yyyyMMdd-HHmmss");
                string trashed = MoveInto(sourcePath, trashRoot, prefix: stamp + "-");
                return new DispositionOutcome(OnSuccess.MoveToTrash, trashed);

            default:
                return new DispositionOutcome(policies.OnSuccess, sourcePath);
        }
    }

    /// <summary>
    /// Moves <paramref name="sourcePath"/> into <paramref name="destDir"/> (created if needed),
    /// keeping its base name (optionally prefixed) and avoiding collisions with a <c>(n)</c> suffix.
    /// </summary>
    private string MoveInto(string sourcePath, string destDir, string? prefix)
    {
        files.CreateDirectory(destDir);
        string fileName = (prefix ?? string.Empty) + Path.GetFileName(sourcePath);
        string dest = Path.Combine(destDir, fileName);

        if (files.FileExists(dest))
            dest = FindFreePath(dest);

        files.Move(sourcePath, dest, overwrite: false);
        return dest;
    }

    private string FindFreePath(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? string.Empty;
        (string stem, string ext) = TokenExpander.SplitName(Path.GetFileName(path));

        int n = 1;
        while (true)
        {
            string candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!files.FileExists(candidate))
                return candidate;
            n++;
        }
    }
}
