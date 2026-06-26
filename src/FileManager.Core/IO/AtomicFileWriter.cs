using System.IO;

namespace FileManager.Core.IO;

/// <summary>
/// Writes a file into place via copy-to-temp + atomic rename (§6.2). The temp is created
/// <b>inside the destination directory</b> so the final rename stays on one volume and is therefore
/// atomic — a Target file is never observed half-written. The copy streams in bounded chunks (§11),
/// so file size does not drive memory use.
/// </summary>
public static class AtomicFileWriter
{
    private const int BufferSize = 1 << 20; // 1 MiB

    /// <summary>
    /// Suffix of the in-flight temp artifacts this writer creates inside a destination directory.
    /// Consumers that enumerate Target contents (e.g.
    /// <see cref="FileManager.Core.Filtering.DedupeIndex"/>) must skip these so a half-written or
    /// orphaned temp is never mistaken for a real Target file.
    /// </summary>
    public const string TempSuffix = ".fmtmp";

    /// <summary>
    /// Copies <paramref name="sourcePath"/> to a temp name in the destination directory, then
    /// atomically renames it to <paramref name="finalDestPath"/>. When <paramref name="overwrite"/>
    /// is true an existing destination is replaced; otherwise the caller must have ensured the
    /// destination is free (see <see cref="FileManager.Core.Routing.ConflictResolver"/>). On any
    /// failure the temp artifact is cleaned up and the exception propagates.
    /// </summary>
    /// <remarks>
    /// Equivalent to <see cref="WriteTemp"/> followed by <see cref="Promote"/>; preserved for callers
    /// that do not need to verify the temp before promoting it (e.g. the transformer working-copy
    /// stage). The Job engine drives the two steps separately so verification can gate the rename.
    /// </remarks>
    public static void Write(IFileOperations files, string sourcePath, string finalDestPath, bool overwrite)
    {
        string tempPath = WriteTemp(files, sourcePath, finalDestPath);
        Promote(files, tempPath, finalDestPath, overwrite);
    }

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into a fresh <see cref="TempSuffix"/> temp file inside the
    /// directory of <paramref name="finalDestPath"/> (created if missing) and returns the temp path —
    /// <b>without</b> renaming it into place. Keeping the temp on the destination volume means the
    /// later <see cref="Promote"/> rename is atomic. On any failure the temp is cleaned up and the
    /// exception propagates. The caller owns the returned temp: it must <see cref="Promote"/> it or
    /// delete it.
    /// </summary>
    public static string WriteTemp(IFileOperations files, string sourcePath, string finalDestPath)
    {
        string destDir = Path.GetDirectoryName(finalDestPath)
            ?? throw new ArgumentException("Destination has no directory.", nameof(finalDestPath));
        files.CreateDirectory(destDir);

        string tempPath = Path.Combine(destDir, "." + Guid.NewGuid().ToString("N") + TempSuffix);
        try
        {
            using Stream src = files.OpenRead(sourcePath);
            using Stream dst = files.OpenWrite(tempPath);
            src.CopyTo(dst, BufferSize);
        }
        catch
        {
            TryCleanup(files, tempPath);
            throw;
        }

        return tempPath;
    }

    /// <summary>
    /// Atomically renames the temp produced by <see cref="WriteTemp"/> to
    /// <paramref name="finalDestPath"/>. When <paramref name="overwrite"/> is true an existing
    /// destination is replaced; otherwise the caller must have ensured it is free. On failure the temp
    /// is cleaned up and the exception propagates.
    /// </summary>
    public static void Promote(IFileOperations files, string tempPath, string finalDestPath, bool overwrite)
    {
        try
        {
            files.Move(tempPath, finalDestPath, overwrite);
        }
        catch
        {
            TryCleanup(files, tempPath);
            throw;
        }
    }

    private static void TryCleanup(IFileOperations files, string tempPath)
    {
        try
        {
            files.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; the original failure is what matters.
        }
    }
}
