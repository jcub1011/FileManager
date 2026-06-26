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
    /// Copies <paramref name="sourcePath"/> to a temp name in the destination directory, then
    /// atomically renames it to <paramref name="finalDestPath"/>. When <paramref name="overwrite"/>
    /// is true an existing destination is replaced; otherwise the caller must have ensured the
    /// destination is free (see <see cref="FileManager.Core.Routing.ConflictResolver"/>). On any
    /// failure the temp artifact is cleaned up and the exception propagates.
    /// </summary>
    public static void Write(IFileOperations files, string sourcePath, string finalDestPath, bool overwrite)
    {
        string destDir = Path.GetDirectoryName(finalDestPath)
            ?? throw new ArgumentException("Destination has no directory.", nameof(finalDestPath));
        files.CreateDirectory(destDir);

        string tempPath = Path.Combine(destDir, "." + Guid.NewGuid().ToString("N") + ".fmtmp");
        try
        {
            using (Stream src = files.OpenRead(sourcePath))
            using (Stream dst = files.OpenWrite(tempPath))
            {
                src.CopyTo(dst, BufferSize);
            }

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
