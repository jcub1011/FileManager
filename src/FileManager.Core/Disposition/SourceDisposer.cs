using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using FileManager.Core.Tokens;

namespace FileManager.Core.Disposition;

/// <summary>The result of disposing of a source file (Phase 6).</summary>
public sealed record DispositionOutcome(OnSuccess Action, string? ResultPath);

/// <summary>
/// Phase 6 source disposition (§4) per <see cref="OnSuccess"/>.
/// </summary>
/// <remarks>
/// <see cref="OnSuccess.MoveToTrash"/> is an M1 <b>placeholder</b>: it performs a recoverable move into
/// a local <c>trash/</c> folder (timestamped to avoid collisions). The native Recycle Bin / FreeDesktop
/// Trash integration lands in M3.
/// </remarks>
public static class SourceDisposer
{
    /// <summary>
    /// Applies the disposition. <paramref name="trashRoot"/> is the placeholder trash folder used for
    /// <see cref="OnSuccess.MoveToTrash"/>; <paramref name="now"/> stamps the trashed name.
    /// </summary>
    public static DispositionOutcome Dispose(
        IFileOperations files,
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
                string archived = MoveInto(files, sourcePath, archive, prefix: null);
                return new DispositionOutcome(OnSuccess.MoveToArchive, archived);

            case OnSuccess.MoveToTrash:
                // Placeholder (M3 replaces with native trash). Stamp to keep trashed names unique.
                string stamp = now.UtcDateTime.ToString("yyyyMMdd-HHmmss");
                string trashed = MoveInto(files, sourcePath, trashRoot, prefix: stamp + "-");
                return new DispositionOutcome(OnSuccess.MoveToTrash, trashed);

            default:
                return new DispositionOutcome(policies.OnSuccess, sourcePath);
        }
    }

    /// <summary>
    /// Moves <paramref name="sourcePath"/> into <paramref name="destDir"/> (created if needed),
    /// keeping its base name (optionally prefixed) and avoiding collisions with a <c>(n)</c> suffix.
    /// </summary>
    private static string MoveInto(IFileOperations files, string sourcePath, string destDir, string? prefix)
    {
        files.CreateDirectory(destDir);
        string fileName = (prefix ?? string.Empty) + Path.GetFileName(sourcePath);
        string dest = Path.Combine(destDir, fileName);

        if (files.FileExists(dest))
            dest = FindFreePath(files, dest);

        files.Move(sourcePath, dest, overwrite: false);
        return dest;
    }

    private static string FindFreePath(IFileOperations files, string path)
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
