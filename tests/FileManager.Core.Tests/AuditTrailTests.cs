using System.IO;
using FileManager.Core.Audit;
using FileManager.Core.Disposition;
using FileManager.Core.Filtering;
using FileManager.Core.IO;
using FileManager.Core.Jobs;
using FileManager.Core.Logging;
using FileManager.Core.Metadata;
using FileManager.Core.Profiles;
using FileManager.Core.Safety;
using FileManager.Core.Sync;
using FileManager.Core.Transformers;
using FileManager.Core.Trash;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Audit-trail acceptance (§7 / §12): every source disposition and every Mirror deletion produces an
/// <see cref="AuditEntry"/> carrying the documented fields (path, action, destination, timestamp, Job).
/// </summary>
public sealed class AuditTrailTests : IDisposable
{
    private static readonly IngestionContext Ctx =
        new() { Now = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero) };

    private readonly TempDir _temp = new("audit");
    private readonly InMemoryLogSink _log = new();
    private readonly SystemFileOperations _files = new();
    private readonly CapturingAuditLog _audit = new();

    public void Dispose() => _temp.Dispose();

    private JobEngine BuildEngine() =>
        new(
            _files,
            _log,
            new JobEngineOptions
            {
                TrashDirectory = _temp.Path("trash"),
                PipelineTempRoot = _temp.Path("pipe"),
                StagingRoot = _temp.Path("staging"),
            },
            processRunner: null,
            journal: null,
            audit: _audit);

    [Fact]
    public void SourceDisposition_PermanentDelete_RecordsAuditEntry()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "x");
        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: TestProfiles.DefaultPolicies with { OnSuccess = OnSuccess.PermanentDelete });

        JobResult r = BuildEngine().ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Closed, r.State);
        AuditEntry entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.PermanentDelete, entry.Action);
        Assert.Equal(PathNormalizer.Normalize(file), PathNormalizer.Normalize(entry.Path));
        Assert.Equal(r.JobId, entry.JobId);
        Assert.Equal(Ctx.Now, entry.Timestamp);
        Assert.Null(entry.Destination);
    }

    [Fact]
    public void SourceDisposition_MoveToTrash_RecordsDestination()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "x");
        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: TestProfiles.DefaultPolicies with { OnSuccess = OnSuccess.MoveToTrash });

        JobResult r = BuildEngine().ProcessFile(p, file, Ctx);

        AuditEntry entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.MoveToTrash, entry.Action);
        // The audited destination mirrors the disposition's result path (the native Recycle Bin may
        // not expose a path, so it can legitimately be null — the audit faithfully records whatever the
        // disposer reported).
        Assert.Equal(r.DispositionPath, entry.Destination);
        Assert.Equal(r.JobId, entry.JobId);
    }

    [Fact]
    public void KeepSource_RecordsKeepSourceAudit()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "x");
        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: TestProfiles.DefaultPolicies with { OnSuccess = OnSuccess.KeepSource });

        BuildEngine().ProcessFile(p, file, Ctx);

        AuditEntry entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.KeepSource, entry.Action);
    }

    [Fact]
    public void AllTargetsSkipped_NoDisposition_NoAuditEntry()
    {
        // No disposition happens when every Target is skipped, so nothing is audited.
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        _temp.WriteFile("T/a.txt", "existing");
        string file = _temp.WriteFile("S/a.txt", "incoming");
        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: TestProfiles.DefaultPolicies with
            {
                OnSuccess = OnSuccess.PermanentDelete,
                ConflictResolution = ConflictResolution.Skip,
            });

        JobResult r = BuildEngine().ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Closed, r.State);
        Assert.Null(r.Disposition);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void MirrorDeletion_RecordsAuditEntry()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        _temp.WriteFile("S/keep.txt", "k");
        _temp.WriteFile("T/keep.txt", "k");
        string surplus = _temp.WriteFile("T/surplus.txt", "remove me");

        var trash = new LocalFolderTrash(_files, FakeFreeSpaceProbe.Unconstrained(), _temp.Path("trash"));
        var planner = new MirrorPlanner(_files, trash, _audit, () => Ctx.Now);
        Profile p = TestProfiles.Build(new[] { s }, new[] { t }) with { SyncMode = SyncMode.Mirror };

        MirrorExecution exec = planner.Reconcile(p);

        Assert.True(exec.AllSucceeded);
        AuditEntry entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditAction.MirrorDeletion, entry.Action);
        Assert.Equal(PathNormalizer.Normalize(surplus), PathNormalizer.Normalize(entry.Path));
        Assert.Equal(p.ProfileId, entry.JobId);
        Assert.Equal(Ctx.Now, entry.Timestamp);
    }

    [Fact]
    public void MirrorDeletion_FailedTrashMove_NotAudited()
    {
        // A trash move that fails leaves the file in place — it is NOT a deletion, so it is not audited.
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        _temp.WriteFile("S/keep.txt", "k");
        _temp.WriteFile("T/keep.txt", "k");
        _temp.WriteFile("T/surplus.txt", "remove me");

        var planner = new MirrorPlanner(_files, new AlwaysFailTrash(), _audit, () => Ctx.Now);
        Profile p = TestProfiles.Build(new[] { s }, new[] { t }) with { SyncMode = SyncMode.Mirror };

        MirrorExecution exec = planner.Reconcile(p);

        Assert.False(exec.AllSucceeded);
        Assert.Empty(_audit.Entries);
    }

    /// <summary>A trash service whose every move fails — for the "failed deletion is not audited" path.</summary>
    private sealed class AlwaysFailTrash : ITrashService
    {
        public TrashResult MoveToTrash(string path) => TrashResult.Failure("forced trash failure");
    }
}
