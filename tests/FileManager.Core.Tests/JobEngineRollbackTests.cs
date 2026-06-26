using System.IO;
using FileManager.Core.Disposition;
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
/// M3 data-safety integration tests: a forced failure at each lifecycle phase must leave the source
/// intact and every Target clean, and under <c>StageOverwrites</c> restore the replaced Target file
/// byte-for-byte (§3.3 / §6.2).
/// </summary>
public sealed class JobEngineRollbackTests : IDisposable
{
    private static readonly IngestionContext Ctx =
        new() { Now = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero) };

    private readonly TempDir _temp = new("rollback");
    private readonly InMemoryLogSink _log = new();
    private readonly SystemFileOperations _files = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A verifier that always fails — to force a Phase 5 verification failure deterministically.</summary>
    private sealed class AlwaysFailVerifier : IVerifier
    {
        public VerificationResult Verify(string finalOutputPath, string targetCopyPath) =>
            VerificationResult.Fail("forced verification failure");
    }

    /// <summary>An IFileOperations decorator that throws on the Nth write-stream open, to inject a Phase 4 I/O fault.</summary>
    private sealed class FailingOnNthWrite(IFileOperations inner, int failOnOpen) : IFileOperations
    {
        private int _opens;

        public Stream OpenWrite(string path)
        {
            _opens++;
            if (_opens == failOnOpen)
                throw new IOException("forced distribution I/O failure");
            return inner.OpenWrite(path);
        }

        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public FileMetadata GetMetadata(string path) => inner.GetMetadata(path);
        public Stream OpenRead(string path) => inner.OpenRead(path);
        public void SetLastWriteTimeUtc(string path, DateTime t) => inner.SetLastWriteTimeUtc(path, t);
        public void Move(string s, string d, bool o) => inner.Move(s, d, o);
        public void Delete(string path) => inner.Delete(path);
        public void DeleteDirectory(string path, bool recursive) => inner.DeleteDirectory(path, recursive);
        public IEnumerable<string> EnumerateFiles(string dir, bool recursive) => inner.EnumerateFiles(dir, recursive);
    }

    private JobEngine BuildEngine(
        IFileOperations files,
        IVerifier? verifier = null,
        ISourceDisposer? disposer = null,
        ITransformerRunner? transformerRunner = null) =>
        new(
            files,
            _log,
            new FilterEvaluator(new DedupeIndex(files)),
            transformerRunner ?? new TransformerRunner(files, new FakeProcessRunner(_ => throw new InvalidOperationException("no steps"))),
            new ConflictResolver(files),
            disposer ?? new SourceDisposer(files),
            verifier,
            new MetadataCopier(files),
            new RollbackEngine(files),
            new JobEngineOptions
            {
                TrashDirectory = _temp.Path("trash"),
                PipelineTempRoot = _temp.Path("pipe"),
                StagingRoot = _temp.Path("staging"),
            });

    private static PolicySet Policies(
        OnSuccess onSuccess = OnSuccess.PermanentDelete,
        OverwriteHandling overwrite = OverwriteHandling.DirectOverwrite,
        VerificationMethod verify = VerificationMethod.None,
        MetadataOnConflict metadata = MetadataOnConflict.WarnAndContinue) =>
        TestProfiles.DefaultPolicies with
        {
            OnSuccess = onSuccess,
            OverwriteHandling = overwrite,
            VerificationMethod = verify,
            MetadataOnConflict = metadata,
        };

    [Fact]
    public void VerificationFailure_RollsBackAllTargets_AndKeepsSource()
    {
        string s = _temp.MakeDir("S");
        string t1 = _temp.MakeDir("T1");
        string t2 = _temp.MakeDir("T2");
        string file = _temp.WriteFile("S/a.txt", "payload");
        Profile p = TestProfiles.Build(new[] { s }, new[] { t1, t2 },
            policies: Policies(onSuccess: OnSuccess.PermanentDelete));

        JobResult r = BuildEngine(_files, verifier: new AlwaysFailVerifier()).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        // Source untouched (not deleted despite PermanentDelete).
        Assert.True(File.Exists(file));
        // Every Target clean — no final file, no leftover temp.
        Assert.False(File.Exists(Path.Combine(t1, "a.txt")));
        Assert.False(File.Exists(Path.Combine(t2, "a.txt")));
        Assert.Empty(Directory.EnumerateFiles(t1));
        Assert.Empty(Directory.EnumerateFiles(t2));
        Assert.Contains(r.Logs, e => e.Code == "ROLLBACK");
    }

    [Fact]
    public void DistributionIoFailure_RollsBackEarlierTargets_AndKeepsSource()
    {
        string s = _temp.MakeDir("S");
        string t1 = _temp.MakeDir("T1");
        string t2 = _temp.MakeDir("T2");
        string file = _temp.WriteFile("S/a.txt", "payload");
        Profile p = TestProfiles.Build(new[] { s }, new[] { t1, t2 },
            policies: Policies(onSuccess: OnSuccess.PermanentDelete));

        // First target's WriteTemp opens the read + write streams; fail the 2nd OpenWrite (the
        // second target's temp), after the first target has been fully placed.
        var failing = new FailingOnNthWrite(_files, failOnOpen: 2);
        JobResult r = BuildEngine(failing).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        Assert.True(File.Exists(file));
        // The first target was placed then rolled back; both targets end clean.
        Assert.False(File.Exists(Path.Combine(t1, "a.txt")));
        Assert.False(File.Exists(Path.Combine(t2, "a.txt")));
        Assert.Empty(Directory.EnumerateFiles(t1));
        Assert.Empty(Directory.EnumerateFiles(t2));
    }

    [Fact]
    public void TransformerFailure_LeavesSourceAndTargetsClean()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "payload");

        // A transformer runner that always aborts the chain (Phase 3 failure).
        var runner = new AbortingTransformerRunner();
        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: Policies(onSuccess: OnSuccess.PermanentDelete)) with
        {
            Transformers = new[] { TestTransformers.InPlace(1, "noop", "") },
        };

        JobResult r = BuildEngine(_files, transformerRunner: runner).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(Path.Combine(t, "a.txt")));
        Assert.Empty(Directory.EnumerateFiles(t));
    }

    [Fact]
    public void StageOverwrites_RestoresReplacedTargetByteForByte_OnPostPlacementFailure()
    {
        // The failure must occur AFTER the prior version has been staged and the new file promoted —
        // a metadata FailJob loss does exactly that (§6.2 / §6.4) — so rollback exercises restore.
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "NEW incoming content");
        // Pre-existing target file that StageOverwrites must restore on rollback.
        string existing = _temp.WriteFile("T/a.txt", "ORIGINAL target content");
        byte[] originalBytes = File.ReadAllBytes(existing);

        var failingMeta = new FailingSetTimeOperations(_files);
        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: Policies(
                onSuccess: OnSuccess.PermanentDelete,
                overwrite: OverwriteHandling.StageOverwrites,
                metadata: MetadataOnConflict.FailJob));

        JobResult r = BuildEngine(failingMeta, verifier: null).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        Assert.True(File.Exists(file));
        // The replaced target was restored byte-for-byte.
        Assert.True(File.Exists(existing));
        Assert.Equal(originalBytes, File.ReadAllBytes(existing));
        Assert.Equal("ORIGINAL target content", File.ReadAllText(existing));
        Assert.Contains(r.Logs, e => e.Code == "STAGED");
        Assert.Contains(r.Logs, e => e.Code == "ROLLBACK");
    }

    [Fact]
    public void StageOverwrites_VerificationFailure_LeavesPriorTargetUntouched()
    {
        // Verification runs BEFORE staging, so a verification failure never disturbs the prior version.
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "NEW incoming content");
        string existing = _temp.WriteFile("T/a.txt", "ORIGINAL target content");
        byte[] originalBytes = File.ReadAllBytes(existing);

        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: Policies(onSuccess: OnSuccess.PermanentDelete, overwrite: OverwriteHandling.StageOverwrites));

        JobResult r = BuildEngine(_files, verifier: new AlwaysFailVerifier()).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        Assert.True(File.Exists(file));
        Assert.Equal(originalBytes, File.ReadAllBytes(existing));
    }

    [Fact]
    public void StageOverwrites_DiscardsStagedOriginal_OnSuccess()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "fresh");
        _temp.WriteFile("T/a.txt", "stale");

        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: Policies(onSuccess: OnSuccess.KeepSource, overwrite: OverwriteHandling.StageOverwrites));

        // Real (passing) None verifier via convenience selection (verifier: null).
        JobResult r = BuildEngine(_files, verifier: null).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Closed, r.State);
        Assert.Equal("fresh", File.ReadAllText(Path.Combine(t, "a.txt")));
        // This Job's staging area was torn down on success (no staged originals left behind).
        string jobStaging = Path.Combine(_temp.Path("staging"), StagingArea.StagingDirName, r.JobId);
        Assert.False(Directory.Exists(jobStaging));
    }

    [Fact]
    public void StageOverwrites_PreservesStagedOriginal_WhenRollbackRestoreFails()
    {
        // A post-placement failure stages + promotes, then rollback restore fails: the staged prior
        // version must NOT be destroyed by the teardown — it stays recoverable and the operator is told.
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "NEW incoming content");
        _temp.WriteFile("T/a.txt", "ORIGINAL target content");

        var failing = new FailMetaAndRestoreOperations(_files);
        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: Policies(
                onSuccess: OnSuccess.PermanentDelete,
                overwrite: OverwriteHandling.StageOverwrites,
                metadata: MetadataOnConflict.FailJob));

        JobResult r = BuildEngine(failing, verifier: null).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        Assert.True(File.Exists(file)); // source untouched
        Assert.Contains(r.Logs, e => e.Code == "STAGING_PRESERVED");

        // The staging area survived teardown and still holds the original content (recoverable).
        string jobStaging = Path.Combine(_temp.Path("staging"), StagingArea.StagingDirName, r.JobId);
        Assert.True(Directory.Exists(jobStaging));
        string[] staged = Directory.GetFiles(jobStaging);
        Assert.Single(staged);
        Assert.Equal("ORIGINAL target content", File.ReadAllText(staged[0]));
    }

    [Fact]
    public void Sha256_CatchesCorruptedTargetCopy_AndRollsBack()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "the true content");

        // A read-corrupting decorator: the copy stream returns different bytes than the source, so the
        // placed temp does not match the final output and SHA256 verification fails.
        var corrupting = new CorruptingFileOperations(_files);
        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: Policies(onSuccess: OnSuccess.PermanentDelete, verify: VerificationMethod.SHA256));

        // verifier: null ⇒ engine selects Sha256Verifier from the profile.
        JobResult r = BuildEngine(corrupting, verifier: null).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(Path.Combine(t, "a.txt")));
        Assert.Empty(Directory.EnumerateFiles(t));
        Assert.Contains(r.Logs, e => e.Code == "VERIFY_FAILED");
    }

    [Fact]
    public void MetadataFailJob_RollsBack()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "payload");

        // A decorator whose SetLastWriteTimeUtc throws ⇒ MetadataCopier reports a detectable loss;
        // FailJob turns that into a rollback.
        var failingMeta = new FailingSetTimeOperations(_files);
        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: Policies(onSuccess: OnSuccess.PermanentDelete, metadata: MetadataOnConflict.FailJob));

        JobResult r = BuildEngine(failingMeta, verifier: null).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(Path.Combine(t, "a.txt")));
        Assert.Empty(Directory.EnumerateFiles(t));
        Assert.Contains(r.Logs, e => e.Code == "METADATA_FAILED");
    }

    [Fact]
    public void MetadataFailJob_MultiTarget_RollsBackAlreadyPlacedTarget()
    {
        string s = _temp.MakeDir("S");
        string t1 = _temp.MakeDir("T1");
        string t2 = _temp.MakeDir("T2");
        string file = _temp.WriteFile("S/a.txt", "payload");

        // Metadata copy succeeds for the first target, then fails on the second; FailJob must roll back
        // the already-completed first target too (§3.3: "including Targets that had already completed").
        var failingMeta = new FailSetTimeOnNthCall(_files, failOnCall: 2);
        Profile p = TestProfiles.Build(new[] { s }, new[] { t1, t2 },
            policies: Policies(onSuccess: OnSuccess.PermanentDelete, metadata: MetadataOnConflict.FailJob));

        JobResult r = BuildEngine(failingMeta, verifier: null).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        Assert.True(File.Exists(file));
        // Both targets clean — the first (completed) placement was removed by rollback.
        Assert.False(File.Exists(Path.Combine(t1, "a.txt")));
        Assert.False(File.Exists(Path.Combine(t2, "a.txt")));
        Assert.Empty(Directory.EnumerateFiles(t1));
        Assert.Empty(Directory.EnumerateFiles(t2));
        Assert.Contains(r.Logs, e => e.Code == "METADATA_FAILED");
    }

    [Fact]
    public void MetadataWarnAndContinue_Proceeds()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "payload");

        var failingMeta = new FailingSetTimeOperations(_files);
        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: Policies(onSuccess: OnSuccess.KeepSource, metadata: MetadataOnConflict.WarnAndContinue));

        JobResult r = BuildEngine(failingMeta, verifier: null).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Closed, r.State);
        Assert.True(File.Exists(Path.Combine(t, "a.txt")));
        Assert.Contains(r.Logs, e => e.Code == "METADATA_WARN");
    }

    // ----- helper fakes -----

    private sealed class AbortingTransformerRunner : ITransformerRunner
    {
        public TransformerChainResult Run(
            TempWorkspace workspace, IReadOnlyList<TransformerStep> steps, string sourcePath, string sourceRoot) =>
            new() { Succeeded = false, FailureReason = "forced transformer abort", Steps = Array.Empty<StepResult>() };
    }

    /// <summary>Returns a read stream that flips bytes, so the placed temp differs from the source.</summary>
    private sealed class CorruptingFileOperations(IFileOperations inner) : IFileOperations
    {
        public Stream OpenRead(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > 0)
                bytes[0] ^= 0xFF; // corrupt the first byte
            return new MemoryStream(bytes);
        }

        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public FileMetadata GetMetadata(string path) => inner.GetMetadata(path);
        public Stream OpenWrite(string path) => inner.OpenWrite(path);
        public void SetLastWriteTimeUtc(string path, DateTime t) => inner.SetLastWriteTimeUtc(path, t);
        public void Move(string sp, string dp, bool o) => inner.Move(sp, dp, o);
        public void Delete(string path) => inner.Delete(path);
        public void DeleteDirectory(string path, bool recursive) => inner.DeleteDirectory(path, recursive);
        public IEnumerable<string> EnumerateFiles(string dir, bool recursive) => inner.EnumerateFiles(dir, recursive);
    }

    private sealed class FailingSetTimeOperations(IFileOperations inner) : IFileOperations
    {
        public void SetLastWriteTimeUtc(string path, DateTime t) =>
            throw new IOException("forced metadata loss");

        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public FileMetadata GetMetadata(string path) => inner.GetMetadata(path);
        public Stream OpenRead(string path) => inner.OpenRead(path);
        public Stream OpenWrite(string path) => inner.OpenWrite(path);
        public void Move(string sp, string dp, bool o) => inner.Move(sp, dp, o);
        public void Delete(string path) => inner.Delete(path);
        public void DeleteDirectory(string path, bool recursive) => inner.DeleteDirectory(path, recursive);
        public IEnumerable<string> EnumerateFiles(string dir, bool recursive) => inner.EnumerateFiles(dir, recursive);
    }

    /// <summary>
    /// Fails every <see cref="SetLastWriteTimeUtc"/> (to trigger a post-placement FailJob rollback) AND
    /// fails the staged-original restore move (a <see cref="Move"/> whose source is under the staging
    /// dir), to exercise the "staging preserved when restore fails" data-safety path.
    /// </summary>
    private sealed class FailMetaAndRestoreOperations(IFileOperations inner) : IFileOperations
    {
        public void SetLastWriteTimeUtc(string path, DateTime t) =>
            throw new IOException("forced metadata loss");

        public void Move(string sp, string dp, bool o)
        {
            if (sp.Contains(StagingArea.StagingDirName, StringComparison.Ordinal))
                throw new IOException("forced restore failure");
            inner.Move(sp, dp, o);
        }

        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public FileMetadata GetMetadata(string path) => inner.GetMetadata(path);
        public Stream OpenRead(string path) => inner.OpenRead(path);
        public Stream OpenWrite(string path) => inner.OpenWrite(path);
        public void Delete(string path) => inner.Delete(path);
        public void DeleteDirectory(string path, bool recursive) => inner.DeleteDirectory(path, recursive);
        public IEnumerable<string> EnumerateFiles(string dir, bool recursive) => inner.EnumerateFiles(dir, recursive);
    }

    /// <summary>Fails the Nth <see cref="SetLastWriteTimeUtc"/> call (1-based), passing the others through.</summary>
    private sealed class FailSetTimeOnNthCall(IFileOperations inner, int failOnCall) : IFileOperations
    {
        private int _calls;

        public void SetLastWriteTimeUtc(string path, DateTime t)
        {
            _calls++;
            if (_calls == failOnCall)
                throw new IOException("forced metadata loss");
            inner.SetLastWriteTimeUtc(path, t);
        }

        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public FileMetadata GetMetadata(string path) => inner.GetMetadata(path);
        public Stream OpenRead(string path) => inner.OpenRead(path);
        public Stream OpenWrite(string path) => inner.OpenWrite(path);
        public void Move(string sp, string dp, bool o) => inner.Move(sp, dp, o);
        public void Delete(string path) => inner.Delete(path);
        public void DeleteDirectory(string path, bool recursive) => inner.DeleteDirectory(path, recursive);
        public IEnumerable<string> EnumerateFiles(string dir, bool recursive) => inner.EnumerateFiles(dir, recursive);
    }
}
