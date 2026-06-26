using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using FileManager.Core.Tokens;

namespace FileManager.Core.Routing;

/// <summary>What happened (or will happen) to a single Target write.</summary>
public enum TargetAction
{
    /// <summary>Written to a previously free destination.</summary>
    Written,

    /// <summary>Replaced an existing destination file.</summary>
    Overwritten,

    /// <summary>Written under an incrementing suffix to avoid an existing file.</summary>
    RenamedSuffix,

    /// <summary>Left the existing file in place; the incoming file was not written.</summary>
    Skipped,
}

/// <summary>
/// The resolved plan for one Target write: the action, the path to write to (null when skipped), and
/// whether the underlying move may replace an existing file.
/// </summary>
public sealed record ConflictOutcome(TargetAction Action, string? FinalPath, bool Overwrite)
{
    /// <summary>The incoming file is skipped; nothing is written.</summary>
    public static ConflictOutcome Skip { get; } = new(TargetAction.Skipped, null, false);
}

/// <summary>
/// Applies <see cref="ConflictResolution"/> at a destination (§3.4). For M:1 aggregation the caller
/// drives Targets in Profile source order, so first-writer-wins under <see cref="ConflictResolution.Overwrite"/>
/// reflects source priority.
/// </summary>
public sealed class ConflictResolver(IFileOperations files) : IConflictResolver
{
    /// <summary>
    /// Resolves how to write a file (whose metadata is <paramref name="incoming"/>) to
    /// <paramref name="destPath"/> under <paramref name="policy"/>. A free destination is always a
    /// plain <see cref="TargetAction.Written"/>.
    /// </summary>
    public ConflictOutcome Resolve(string destPath, FileMetadata incoming, ConflictResolution policy)
    {
        if (!files.FileExists(destPath))
            return new ConflictOutcome(TargetAction.Written, destPath, Overwrite: false);

        switch (policy)
        {
            case ConflictResolution.Overwrite:
                return new ConflictOutcome(TargetAction.Overwritten, destPath, Overwrite: true);

            case ConflictResolution.OverwriteIfNewer:
                FileMetadata existing = files.GetMetadata(destPath);
                return incoming.LastWriteTimeUtc > existing.LastWriteTimeUtc
                    ? new ConflictOutcome(TargetAction.Overwritten, destPath, Overwrite: true)
                    : ConflictOutcome.Skip;

            case ConflictResolution.RenameSuffix:
                string renamed = FindFreeSuffixedPath(destPath);
                return new ConflictOutcome(TargetAction.RenamedSuffix, renamed, Overwrite: false);

            case ConflictResolution.Skip:
                return ConflictOutcome.Skip;

            default:
                return ConflictOutcome.Skip;
        }
    }

    /// <summary>
    /// Produces the first free <c>name (n).ext</c> beside <paramref name="destPath"/>, incrementing
    /// from 1. Stem/extension splitting is shared with <see cref="TokenExpander"/> so naming stays
    /// consistent with token expansion.
    /// </summary>
    private string FindFreeSuffixedPath(string destPath)
    {
        string dir = Path.GetDirectoryName(destPath) ?? string.Empty;
        string fileName = Path.GetFileName(destPath);
        (string stem, string ext) = TokenExpander.SplitName(fileName);

        int n = 1;
        while (true)
        {
            string candidateName = $"{stem} ({n}){ext}";
            string candidate = Path.Combine(dir, candidateName);
            if (!files.FileExists(candidate))
                return candidate;
            n++;
        }
    }
}
