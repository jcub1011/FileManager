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
using FileManager.Core.Trash;
using FileManager.Core.Verification;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Proactive (pre-flight) disk-space tests for the <see cref="JobEngine"/>: a constrained Target volume
/// fails the Job fast leaving the source intact and the Target clean, a successful Job releases its
/// reservation so a later Job fits, cross-volume staging is accounted for, and a full trash volume
/// fails a <c>MoveToTrash</c> disposition (source preserved).
/// </summary>
public sealed class JobEngineSpaceTests : IDisposable
{
    private static readonly IngestionContext Ctx =
        new() { Now = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero) };

    private readonly TempDir _temp = new("space");
    private readonly InMemoryLogSink _log = new();
    private readonly SystemFileOperations _files = new();

    public void Dispose() => _temp.Dispose();

    private JobEngine BuildEngine(
        IFreeSpaceProbe probe,
        SpaceReservationLedger? ledger = null,
        ITrashService? trash = null) =>
        new(
            _files,
            _log,
            new FilterEvaluator(new DedupeIndex(_files)),
            new TransformerRunner(_files, new FakeProcessRunner(_ => throw new InvalidOperationException("no steps"))),
            new ConflictResolver(_files),
            new SourceDisposer(_files, trash),
            verifier: null,
            new MetadataCopier(_files),
            new RollbackEngine(_files),
            probe,
            ledger ?? new SpaceReservationLedger(probe),
            new JobEngineOptions
            {
                TrashDirectory = _temp.Path("trash"),
                PipelineTempRoot = _temp.Path("pipe"),
                StagingRoot = _temp.Path("staging"),
            });

    private static PolicySet Policies(
        OnSuccess onSuccess = OnSuccess.KeepSource,
        OverwriteHandling overwrite = OverwriteHandling.DirectOverwrite) =>
        TestProfiles.DefaultPolicies with { OnSuccess = onSuccess, OverwriteHandling = overwrite };

    [Fact]
    public void ConstrainedTargetVolume_FailsFast_NoPartialFiles_SourceIntact()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "payload-bytes"); // 13 bytes

        // The Target volume only has 5 bytes free — too small for the 13-byte file.
        var probe = new FakeFreeSpaceProbe(new Dictionary<string, long> { [t] = 5 });
        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: Policies(onSuccess: OnSuccess.PermanentDelete));

        JobResult r = BuildEngine(probe).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        Assert.Contains("space", r.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(r.Logs, e => e.Code == "NO_SPACE");
        // Source untouched (not deleted despite PermanentDelete).
        Assert.True(File.Exists(file));
        // Target dir clean — no final file, no leftover temp (nothing was written).
        Assert.False(File.Exists(Path.Combine(t, "a.txt")));
        Assert.Empty(Directory.EnumerateFiles(t));
    }

    [Fact]
    public void SuccessfulJob_ReleasesReservation_SoNextJobFits()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string fileA = _temp.WriteFile("S/a.txt", "12345"); // 5 bytes
        string fileB = _temp.WriteFile("S/b.txt", "67890"); // 5 bytes

        // Only enough room for ONE outstanding 5-byte reservation at a time. A per-engine ledger that
        // releases after each Job lets the second Job fit; if release were broken, the second would fail.
        var probe = new FakeFreeSpaceProbe(new Dictionary<string, long> { [t] = 5 });
        var ledger = new SpaceReservationLedger(probe);
        JobEngine engine = BuildEngine(probe, ledger);

        Profile p = TestProfiles.Build(new[] { s }, new[] { t }, policies: Policies(onSuccess: OnSuccess.KeepSource));

        JobResult r1 = engine.ProcessFile(p, fileA, Ctx);
        Assert.Equal(JobState.Closed, r1.State);

        JobResult r2 = engine.ProcessFile(p, fileB, Ctx);
        Assert.Equal(JobState.Closed, r2.State);
        Assert.True(File.Exists(Path.Combine(t, "b.txt")));
    }

    [Fact]
    public void OutstandingReservation_BlocksSecondConcurrentJob_OnSharedLedger()
    {
        // A shared ledger across two engines (the M5 worker-pool shape). The first reservation is held
        // by never letting the first Job complete: we model that by reserving directly, then driving a
        // Job whose Target volume is now full because of the standing reservation.
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "1234567890"); // 10 bytes

        var probe = new FakeFreeSpaceProbe(new Dictionary<string, long> { [t] = 12 });
        var ledger = new SpaceReservationLedger(probe);

        // Standing reservation of 10 bytes on the Target volume (a concurrent in-flight Job).
        ReservationResult standing = ledger.TryReserve(new[] { new SpaceRequest(Path.Combine(t, "x"), 10) });
        Assert.True(standing.Ok);

        Profile p = TestProfiles.Build(new[] { s }, new[] { t }, policies: Policies(onSuccess: OnSuccess.KeepSource));
        JobResult r = BuildEngine(probe, ledger).ProcessFile(p, file, Ctx);

        // 12 free - 10 reserved = 2 < 10 needed ⇒ the Job fails for space.
        Assert.Equal(JobState.Failed, r.State);
        Assert.Contains(r.Logs, e => e.Code == "NO_SPACE");
        Assert.True(File.Exists(file));

        // Releasing the standing reservation lets a retry succeed.
        standing.Handle!.Dispose();
        JobResult retry = BuildEngine(probe, ledger).ProcessFile(p, file, Ctx);
        Assert.Equal(JobState.Closed, retry.State);
    }

    [Fact]
    public void StagingAccounting_CrossVolumeStaging_ConstrainedStagingVolume_FailsFast()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string staging = _temp.MakeDir("staging");
        string file = _temp.WriteFile("S/a.txt", "NEW"); // 3 bytes incoming
        // A pre-existing target file (20 bytes) that StageOverwrites would move aside cross-volume.
        _temp.WriteFile("T/a.txt", "EXISTING-20-BYTES!!!");

        // Target volume has plenty for the 3-byte incoming file; the DISTINCT staging volume has only
        // 5 bytes — too little for the 20-byte prior version's cross-volume staging move.
        var probe = new FakeFreeSpaceProbe(new Dictionary<string, long>
        {
            [t] = 1000,
            [staging] = 5,
        });

        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: Policies(onSuccess: OnSuccess.KeepSource, overwrite: OverwriteHandling.StageOverwrites));

        JobResult r = BuildEngine(probe).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        Assert.Contains(r.Logs, e => e.Code == "NO_SPACE");
        Assert.True(File.Exists(file));
        // Prior target version untouched — nothing was staged or overwritten.
        Assert.Equal("EXISTING-20-BYTES!!!", File.ReadAllText(Path.Combine(t, "a.txt")));
    }

    [Fact]
    public void FullTrashVolume_FailsMoveToTrashDisposition_SourcePreserved()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string trashRoot = _temp.MakeDir("localtrash");
        string file = _temp.WriteFile("S/a.txt", "payload-bytes"); // 13 bytes

        // Target volume is roomy; the trash volume is full (0 free). The Job places the Target file but
        // then fails MoveToTrash disposition — and the source must be preserved (§3.1.1).
        var probe = new FakeFreeSpaceProbe(new Dictionary<string, long>
        {
            [t] = 1000,
            [trashRoot] = 0,
        });
        var trash = new LocalFolderTrash(_files, probe, trashRoot);

        Profile p = TestProfiles.Build(new[] { s }, new[] { t }, policies: Policies(onSuccess: OnSuccess.MoveToTrash));

        JobResult r = BuildEngine(probe, trash: trash).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        // The source still exists (trash move refused, disposition failed → Job failed).
        Assert.True(File.Exists(file));
        // Nothing landed in the trash folder.
        Assert.Empty(Directory.EnumerateFiles(trashRoot));
    }
}
