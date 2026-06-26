using FileManager.Core.Profiles;

namespace FileManager.Core.Transformers;

/// <summary>
/// Drives a Profile's ordered transformer chain (spec §4 Phase 3) on an isolated working copy of the
/// Source. Substitutable in the <see cref="FileManager.Core.Jobs.JobEngine"/> so the orchestrator can
/// be tested without spawning real transformer subprocesses.
/// </summary>
public interface ITransformerRunner
{
    /// <summary>
    /// Runs <paramref name="steps"/> against <paramref name="sourcePath"/> inside
    /// <paramref name="workspace"/>. <paramref name="sourceRoot"/> is the owning Source root used for
    /// the <c>$source_root_path</c> token.
    /// </summary>
    public TransformerChainResult Run(
        TempWorkspace workspace,
        IReadOnlyList<TransformerStep> steps,
        string sourcePath,
        string sourceRoot);
}
