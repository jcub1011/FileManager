using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using FileManager.Core.Tokens;
using FileManager.Core.Trash;

namespace FileManager.Core.Disposition;

/// <summary>The result of disposing of a source file (Phase 6).</summary>
public sealed record DispositionOutcome(OnSuccess Action, string? ResultPath);

/// <summary>
/// The pure, side-effect-free decision of what a Phase 6 disposition <b>would</b> do to a source file:
/// the <see cref="OnSuccess"/> action and, for the move dispositions, the destination <em>folder</em>
/// the file would land in. It deliberately does not resolve the final collision-suffixed file name
/// (that requires probing the filesystem at execution time) — it captures the policy decision, not the
/// I/O. Shared by the live <see cref="SourceDisposer"/> and the M7 dry-run so the preview cannot drift
/// from reality.
/// </summary>
/// <param name="Action">The disposition action that would run.</param>
/// <param name="DestinationFolder">
/// The folder a <see cref="OnSuccess.MoveToTrash"/> / <see cref="OnSuccess.MoveToArchive"/> would move
/// the file into; null for <see cref="OnSuccess.KeepSource"/> and <see cref="OnSuccess.PermanentDelete"/>.
/// </param>
public sealed record DispositionDecision(OnSuccess Action, string? DestinationFolder);

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
    /// The pure decision of what <see cref="Dispose"/> <b>would</b> do for these policies, without
    /// performing (or planning the precise collision-suffixed name of) any move/delete. The live
    /// <see cref="Dispose"/> below is implemented in terms of these same branches, and the M7 dry-run
    /// calls this directly, so the preview and the real disposition can never disagree about the action
    /// or the destination folder. A native <see cref="ITrashService"/>'s exact trashed path is not
    /// knowable up front, so a trash decision reports the local-fallback <paramref name="trashRoot"/>
    /// folder as the destination (the engine notes the native Recycle Bin / FreeDesktop Trash applies).
    /// </summary>
    public static DispositionDecision PreviewDisposition(PolicySet policies, string trashRoot) =>
        policies.OnSuccess switch
        {
            OnSuccess.KeepSource => new DispositionDecision(OnSuccess.KeepSource, null),
            OnSuccess.PermanentDelete => new DispositionDecision(OnSuccess.PermanentDelete, null),
            OnSuccess.MoveToArchive => new DispositionDecision(
                OnSuccess.MoveToArchive,
                policies.ArchiveFolder
                    ?? throw new InvalidOperationException("MoveToArchive requires ArchiveFolder.")),
            OnSuccess.MoveToTrash => new DispositionDecision(OnSuccess.MoveToTrash, trashRoot),
            _ => new DispositionDecision(policies.OnSuccess, null),
        };

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
        // The POLICY decision (which action, and the destination folder for a move — including the
        // MoveToArchive ArchiveFolder requirement) lives in exactly ONE place: the shared
        // PreviewDisposition. This method only performs the I/O for that decision, so the live disposer
        // and the M7 dry-run can never diverge on what an OnSuccess policy means.
        DispositionDecision decision = PreviewDisposition(policies, trashRoot);
        switch (decision.Action)
        {
            case OnSuccess.KeepSource:
                return new DispositionOutcome(OnSuccess.KeepSource, sourcePath);

            case OnSuccess.PermanentDelete:
                files.Delete(sourcePath);
                return new DispositionOutcome(OnSuccess.PermanentDelete, null);

            case OnSuccess.MoveToArchive:
                // DestinationFolder is non-null here: PreviewDisposition already enforced ArchiveFolder.
                string archived = MoveInto(sourcePath, decision.DestinationFolder!, prefix: null);
                return new DispositionOutcome(OnSuccess.MoveToArchive, archived);

            case OnSuccess.MoveToTrash:
                if (_trash is not null)
                {
                    TrashResult result = _trash.MoveToTrash(sourcePath);
                    if (!result.Ok)
                        throw new IOException($"MoveToTrash failed: {result.Reason}");
                    return new DispositionOutcome(OnSuccess.MoveToTrash, result.TrashedPath);
                }

                // Local-folder fallback (no native trash injected). The decision's DestinationFolder is
                // the trash root; stamp to keep trashed names unique.
                string stamp = now.UtcDateTime.ToString("yyyyMMdd-HHmmss");
                string trashed = MoveInto(sourcePath, decision.DestinationFolder ?? trashRoot, prefix: stamp + "-");
                return new DispositionOutcome(OnSuccess.MoveToTrash, trashed);

            default:
                return new DispositionOutcome(decision.Action, sourcePath);
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
