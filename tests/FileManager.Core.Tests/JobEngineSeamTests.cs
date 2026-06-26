using System.IO;
using FileManager.Core.Disposition;
using FileManager.Core.Filtering;
using FileManager.Core.IO;
using FileManager.Core.Jobs;
using FileManager.Core.Logging;
using FileManager.Core.Profiles;
using FileManager.Core.Routing;
using FileManager.Core.Transformers;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Exercises the <see cref="JobEngine"/> full constructor: each phase collaborator
/// (<see cref="IFilterEvaluator"/>, <see cref="IConflictResolver"/>, …) is now an injectable interface,
/// so the orchestration can be driven with fakes instead of the real routing/filtering logic.
/// </summary>
public sealed class JobEngineSeamTests : IDisposable
{
    private static readonly IngestionContext Ctx =
        new() { Now = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero) };

    private readonly TempDir _temp = new("seam");
    private readonly InMemoryLogSink _log = new();
    private readonly SystemFileOperations _files = new();

    public void Dispose() => _temp.Dispose();

    private JobEngine BuildEngine(
        IFilterEvaluator? evaluator = null,
        IConflictResolver? conflictResolver = null) =>
        new(
            _files,
            _log,
            evaluator ?? new FilterEvaluator(new DedupeIndex(_files)),
            new TransformerRunner(_files, new FakeProcessRunner(_ => throw new InvalidOperationException("no steps"))),
            conflictResolver ?? new ConflictResolver(_files),
            new SourceDisposer(_files),
            new JobEngineOptions { TrashDirectory = _temp.Path("trash"), PipelineTempRoot = _temp.Path("pipe") });

    /// <summary>A filter evaluator that rejects everything with a fixed deciding-filter name.</summary>
    private sealed class RejectAllEvaluator : IFilterEvaluator
    {
        public FilterDecision Evaluate(
            FilterSet filters, FilterCandidate candidate, IReadOnlyList<TargetSpec> targets, DateTimeOffset now) =>
            FilterDecision.Reject("FakeFilter");
    }

    /// <summary>A conflict resolver that always skips, regardless of the real destination state.</summary>
    private sealed class AlwaysSkipResolver : IConflictResolver
    {
        public ConflictOutcome Resolve(string destPath, FileMetadata incoming, ConflictResolution policy) =>
            ConflictOutcome.Skip;
    }

    [Fact]
    public void InjectedFilterEvaluator_DrivesTheScreeningDecision()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "x");
        // No Include filter — the real evaluator would pass this file; the injected fake rejects it.
        Profile p = TestProfiles.Build(new[] { s }, new[] { t });

        JobResult r = BuildEngine(evaluator: new RejectAllEvaluator()).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Skipped, r.State);
        Assert.Equal("FakeFilter", r.DecidingFilter);
        Assert.False(File.Exists(Path.Combine(t, "a.txt")));
    }

    [Fact]
    public void InjectedConflictResolver_DrivesTheDistributionOutcome()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        // No pre-existing target file — the real resolver would write it; the injected fake skips.
        string file = _temp.WriteFile("S/a.txt", "incoming");
        PolicySet policies = TestProfiles.DefaultPolicies with { OnSuccess = OnSuccess.PermanentDelete };
        Profile p = TestProfiles.Build(new[] { s }, new[] { t }, policies: policies);

        JobResult r = BuildEngine(conflictResolver: new AlwaysSkipResolver()).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Closed, r.State);
        Assert.Equal(TargetAction.Skipped, r.Targets[0].Action);
        Assert.False(File.Exists(Path.Combine(t, "a.txt")));
        // All targets skipped ⇒ source is preserved even under PermanentDelete.
        Assert.Null(r.Disposition);
        Assert.True(File.Exists(file));
    }
}
