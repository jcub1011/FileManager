using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Profiles;

namespace FileManager.Core.Filtering;

/// <summary>
/// Content-hash dedupe (§5.1 <c>ContentHashDedupe</c>): a file is screened out if a byte-identical
/// file already exists in any Target.
/// </summary>
/// <remarks>
/// Appendix B open item — M1 <b>computes on demand</b> (hash the source, then stream-hash each Target
/// file) rather than maintaining a persisted Target index. This is O(files × size) per scan; a cached
/// index is a later optimization that can be slotted in behind <see cref="IDedupeIndex"/>.
/// </remarks>
public sealed class DedupeIndex(IFileOperations files) : IDedupeIndex
{
    /// <summary>
    /// Whether a file with the same SHA256 as <paramref name="sourcePath"/> already exists under any
    /// of <paramref name="targets"/>. Unreadable Target files are skipped rather than failing the check.
    /// </summary>
    public bool ExistsInTargets(string sourcePath, IReadOnlyList<TargetSpec> targets)
    {
        string sourceHash = HashUtil.ComputeSha256(files, sourcePath);

        foreach (TargetSpec target in targets)
        {
            foreach (string candidate in files.EnumerateFiles(target.Path, recursive: true))
            {
                // Skip in-flight/orphaned atomic-write temps — they are byte-copies of a source and
                // would otherwise produce a spurious duplicate match.
                if (candidate.EndsWith(AtomicFileWriter.TempSuffix, StringComparison.Ordinal))
                    continue;

                try
                {
                    if (HashUtil.ComputeSha256(files, candidate) == sourceHash)
                        return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A Target file we can't read can't be proven a duplicate; keep scanning.
                }
            }
        }

        return false;
    }
}
