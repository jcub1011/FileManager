using FileManager.Core.Profiles;

namespace FileManager.Core.Filtering;

/// <summary>
/// Content-hash dedupe seam (§5.1 <c>ContentHashDedupe</c>): decides whether a source file is a
/// byte-for-byte duplicate of a file already present in any Target. The M1 implementation
/// (<see cref="DedupeIndex"/>) computes hashes on demand; a persisted-index implementation can be
/// substituted later behind this contract.
/// </summary>
public interface IDedupeIndex
{
    /// <summary>
    /// Whether a file with the same SHA256 as <paramref name="sourcePath"/> already exists under any
    /// of <paramref name="targets"/>.
    /// </summary>
    public bool ExistsInTargets(string sourcePath, IReadOnlyList<TargetSpec> targets);
}
