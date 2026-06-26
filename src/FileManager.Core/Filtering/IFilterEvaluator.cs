using FileManager.Core.Profiles;

namespace FileManager.Core.Filtering;

/// <summary>
/// Phase 2 filter screening (§5.1): screens a candidate file against a resolved <see cref="FilterSet"/>
/// and reports the first rejecting rule. Reusable by the M7 dry-run report and substitutable in the
/// <see cref="FileManager.Core.Jobs.JobEngine"/> for isolated orchestrator tests.
/// </summary>
public interface IFilterEvaluator
{
    /// <summary>
    /// Screens <paramref name="candidate"/> against <paramref name="filters"/>. <paramref name="now"/>
    /// anchors the age rules; <paramref name="targets"/> is scanned only when dedupe is enabled.
    /// </summary>
    public FilterDecision Evaluate(
        FilterSet filters,
        FilterCandidate candidate,
        IReadOnlyList<TargetSpec> targets,
        DateTimeOffset now);
}
