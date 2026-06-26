using System.IO;
using FileManager.Core.Disposition;
using FileManager.Core.Execution;
using FileManager.Core.Filtering;
using FileManager.Core.IO;
using FileManager.Core.Jobs;
using FileManager.Core.Logging;
using FileManager.Core.Metadata;
using FileManager.Core.Profiles;
using FileManager.Core.Routing;
using FileManager.Core.Safety;
using FileManager.Core.Transformers;
using FileManager.Core.Verification;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Verifies the M5 intra-Job multi-Target parallelism: with <c>targetParallelism &gt; 1</c> a Job writes
/// every Target (in Profile order), and a failure on any Target still rolls back EVERY Target — the
/// rollback/disposition semantics are identical to the sequential engine.
/// </summary>
public sealed class JobEngineParallelTargetTests : IDisposable
{
    private static readonly IngestionContext Ctx =
        new() { Now = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero) };

    private readonly TempDir _temp = new("partarget");
    private readonly InMemoryLogSink _log = new();
    private readonly SystemFileOperations _files = new();

    public void Dispose() => _temp.Dispose();

    private sealed class AlwaysFailVerifier : IVerifier
    {
        public VerificationResult Verify(string finalOutputPath, string targetCopyPath) =>
            VerificationResult.Fail("forced verification failure");
    }

    private JobEngine BuildEngine(int parallelism, IVerifier? verifier = null) =>
        new(
            _files,
            _log,
            new FilterEvaluator(new DedupeIndex(_files)),
            new TransformerRunner(_files, new FakeProcessRunner(_ => throw new InvalidOperationException("no steps"))),
            new ConflictResolver(_files),
            new SourceDisposer(_files),
            verifier,
            new MetadataCopier(_files),
            new RollbackEngine(_files),
            FakeFreeSpaceProbe.Unconstrained(),
            new SpaceReservationLedger(FakeFreeSpaceProbe.Unconstrained()),
            new JobEngineOptions
            {
                TrashDirectory = _temp.Path("trash"),
                PipelineTempRoot = _temp.Path("pipe"),
                StagingRoot = _temp.Path("staging"),
            },
            journal: null,
            audit: null,
            pathLocks: new PathLockManager(),
            targetParallelism: parallelism);

    [Fact]
    public void ParallelTargets_AllWritten_InProfileOrder()
    {
        string s = _temp.MakeDir("S");
        var targets = new List<string>();
        for (int i = 0; i < 6; i++)
            targets.Add(_temp.MakeDir($"T{i}"));
        string file = _temp.WriteFile("S/a.txt", "payload");

        Profile p = TestProfiles.Build(new[] { s }, targets);
        JobResult r = BuildEngine(parallelism: 4).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Closed, r.State);
        Assert.Equal(targets.Count, r.Targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            Assert.True(File.Exists(Path.Combine(targets[i], "a.txt")));
            // Outcomes are positional ⇒ order matches the Profile's Targets.
            Assert.Equal(PathNormalizer.Normalize(targets[i]), PathNormalizer.Normalize(r.Targets[i].TargetRoot));
        }
    }

    [Fact]
    public void ParallelTargets_VerificationFailure_RollsBackEveryTarget_SourceIntact()
    {
        string s = _temp.MakeDir("S");
        var targets = new List<string>();
        for (int i = 0; i < 5; i++)
            targets.Add(_temp.MakeDir($"T{i}"));
        string file = _temp.WriteFile("S/a.txt", "payload");

        PolicySet policies = TestProfiles.DefaultPolicies with { OnSuccess = OnSuccess.PermanentDelete };
        Profile p = TestProfiles.Build(new[] { s }, targets, policies: policies);

        JobResult r = BuildEngine(parallelism: 4, verifier: new AlwaysFailVerifier()).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        // Whole-file rollback: no Target keeps a placed file, and the source is left in place.
        foreach (string t in targets)
            Assert.False(File.Exists(Path.Combine(t, "a.txt")), $"{t} should have been rolled back");
        Assert.True(File.Exists(file), "source must be untouched on failure");
    }
}
