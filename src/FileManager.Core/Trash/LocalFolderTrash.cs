using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Tokens;

namespace FileManager.Core.Trash;

/// <summary>
/// A portable, recoverable soft-delete that moves files into a local <c>trash/</c> folder (the M1
/// placeholder behavior), used as the fallback on platforms without a native trash integration and as
/// the deterministic implementation under test. Names are kept (with a <c>(n)</c> suffix on collision)
/// so a trashed file is recoverable by inspection.
/// </summary>
public sealed class LocalFolderTrash(IFileOperations files, IFreeSpaceProbe freeSpace, string trashRoot, long marginBytes = 0) : ITrashService
{
    public TrashResult MoveToTrash(string path)
    {
        try
        {
            // Proactive free-space check against the trash destination volume (trashRoot). A
            // cross-volume move copies bytes, so a full trash volume fails the move; catch it up front.
            // Trash moves are terminal and quick, so this is a direct probe check, not ledger-accounted.
            long size = files.GetMetadata(path).Length;
            long available = freeSpace.Probe(trashRoot).AvailableBytes;
            long usable = available == long.MaxValue ? long.MaxValue : Math.Max(0L, available - marginBytes);
            if (size > usable)
                return TrashResult.Failure($"insufficient space on trash volume {trashRoot} (need {size}, available {usable}).");

            files.CreateDirectory(trashRoot);
            string dest = Path.Combine(trashRoot, Path.GetFileName(path));
            if (files.FileExists(dest))
                dest = FindFreePath(dest);

            files.Move(path, dest, overwrite: false);
            return TrashResult.Success(dest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TrashResult.Failure(ex.Message);
        }
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
