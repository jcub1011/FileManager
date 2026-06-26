using FileManager.Core.IO;
using FileManager.Core.Profiles;

namespace FileManager.Core.Routing;

/// <summary>
/// Resolves a <see cref="ConflictResolution"/> policy at a Target destination (§3.4), producing the
/// plan for a single write. Substitutable in the <see cref="FileManager.Core.Jobs.JobEngine"/> so the
/// orchestrator's distribution loop can be tested without real routing logic.
/// </summary>
public interface IConflictResolver
{
    /// <summary>
    /// Resolves how to write a file (whose metadata is <paramref name="incoming"/>) to
    /// <paramref name="destPath"/> under <paramref name="policy"/>. A free destination is always a
    /// plain <see cref="TargetAction.Written"/>.
    /// </summary>
    public ConflictOutcome Resolve(string destPath, FileMetadata incoming, ConflictResolution policy);
}
