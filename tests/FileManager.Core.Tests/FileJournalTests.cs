using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Journal;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// <see cref="FileJournal"/> behavior: open-entry reconstruction, the Closed exclusion, torn-tail
/// tolerance, fsync-per-record (via the fault-injecting double), and compaction of closed Jobs.
/// </summary>
public sealed class FileJournalTests : IDisposable
{
    private readonly TempDir _temp = new("journal");

    public void Dispose() => _temp.Dispose();

    private static JournalRecord Open(string jobId) => new()
    {
        SchemaVersion = JournalRecord.CurrentSchemaVersion,
        Event = JournalEventType.Open,
        JobId = jobId,
        ProfileId = "prof",
        SourcePath = @"C:\src\a.txt",
        Timestamp = DateTimeOffset.UnixEpoch,
        Disposition = OnSuccess.PermanentDelete,
    };

    private static JournalRecord Event(string jobId, JournalEventType evt, string? temp = null, string? final = null,
        string? staged = null, string? stagedOrig = null) => new()
    {
        SchemaVersion = JournalRecord.CurrentSchemaVersion,
        Event = evt,
        JobId = jobId,
        ProfileId = "prof",
        SourcePath = @"C:\src\a.txt",
        Timestamp = DateTimeOffset.UnixEpoch,
        TempPath = temp,
        FinalPath = final,
        StagedPath = staged,
        StagedOriginalPath = stagedOrig,
    };

    [Fact]
    public void OpenThenClose_NotReportedAsOpen()
    {
        string path = _temp.Path("jobs.journal");
        using (var journal = new FileJournal(path))
        {
            journal.Open(Open("j1"));
            journal.Record(Event("j1", JournalEventType.AllTargetsVerified));
            journal.Close("j1");
        }

        using var reopened = new FileJournal(path);
        Assert.Empty(reopened.ReadOpenEntries());
    }

    [Fact]
    public void OpenWithoutClose_ReportedAsOpen_WithAccumulatedArtifacts()
    {
        string path = _temp.Path("jobs.journal");
        using var journal = new FileJournal(path);
        journal.Open(Open("j1"));
        journal.Record(Event("j1", JournalEventType.TargetVerified, temp: @"C:\t\.tmp1"));
        journal.Record(Event("j1", JournalEventType.TargetStaged, staged: @"C:\stage\0-a.txt", stagedOrig: @"C:\t\a.txt"));
        journal.Record(Event("j1", JournalEventType.TargetPlaced, temp: @"C:\t\.tmp1", final: @"C:\t\a.txt"));

        IReadOnlyList<OpenJobState> open = journal.ReadOpenEntries();

        OpenJobState job = Assert.Single(open);
        Assert.Equal("j1", job.JobId);
        Assert.Equal(JournalEventType.TargetPlaced, job.LastEvent);
        Assert.True(job.InPlacement);
        // The temp was promoted, so it is no longer an unpromoted orphan; the final is recorded.
        Assert.Empty(job.UnpromotedTemps);
        Assert.Equal(new[] { @"C:\t\a.txt" }, job.PlacedFinals);
        Assert.Single(job.StagedOriginals);
        Assert.Equal(@"C:\stage\0-a.txt", job.StagedOriginals[0].StagedPath);
        Assert.Equal(OnSuccess.PermanentDelete, job.Disposition);
    }

    [Fact]
    public void PreplacementOpen_IsNotInPlacement()
    {
        string path = _temp.Path("jobs.journal");
        using var journal = new FileJournal(path);
        journal.Open(Open("j1"));
        journal.Record(Event("j1", JournalEventType.Screened));
        journal.Record(Event("j1", JournalEventType.SpaceReserved));

        OpenJobState job = Assert.Single(journal.ReadOpenEntries());
        Assert.False(job.InPlacement);
    }

    [Fact]
    public void AllTargetsVerified_IsTracked()
    {
        string path = _temp.Path("jobs.journal");
        using var journal = new FileJournal(path);
        journal.Open(Open("j1"));
        journal.Record(Event("j1", JournalEventType.TargetPlaced, final: @"C:\t\a.txt"));
        journal.Record(Event("j1", JournalEventType.AllTargetsVerified));

        OpenJobState job = Assert.Single(journal.ReadOpenEntries());
        Assert.True(job.AllTargetsVerified);
    }

    [Fact]
    public void Fsync_CalledOncePerRecord()
    {
        var writer = new FaultInjectingDurableWriter();
        var journal = new FileJournal(_temp.Path("jobs.journal"), writerFactory: _ => writer);

        journal.Open(Open("j1"));                                       // 1 flush
        journal.Record(Event("j1", JournalEventType.Screened));         // 2
        journal.Record(Event("j1", JournalEventType.AllTargetsVerified)); // 3
        journal.Close("j1");                                            // 4

        Assert.Equal(4, writer.FlushCount);
    }

    [Fact]
    public void TornTailRecord_IsSkipped_OpenEntryStillRecovered()
    {
        string path = _temp.Path("jobs.journal");

        // Write a valid Open record, then append a torn (truncated) frame by hand to the file.
        using (var journal = new FileJournal(path))
            journal.Open(Open("j1"));

        byte[] torn = JournalFraming.Encode(Event("j1", JournalEventType.AllTargetsVerified))[..^4];
        using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            fs.Write(torn);

        using var reopened = new FileJournal(path);
        OpenJobState job = Assert.Single(reopened.ReadOpenEntries());
        // The torn AllTargetsVerified was ignored; the Job remains open at its last good event.
        Assert.Equal(JournalEventType.Open, job.LastEvent);
        Assert.False(job.AllTargetsVerified);
    }

    [Fact]
    public void Compaction_DropsClosedJobs_KeepsOpenOnes()
    {
        string path = _temp.Path("jobs.journal");
        // A tiny rotation threshold forces compaction on the next append.
        using var journal = new FileJournal(path, rotationSizeBytes: 64);

        journal.Open(Open("closed"));
        journal.Record(Event("closed", JournalEventType.AllTargetsVerified));
        journal.Close("closed");          // this Job becomes droppable
        journal.Open(Open("survivor"));   // by now the file is well over 64 bytes ⇒ compaction runs

        IReadOnlyList<OpenJobState> open = journal.ReadOpenEntries();
        OpenJobState job = Assert.Single(open);
        Assert.Equal("survivor", job.JobId);

        // The file should be physically smaller than before compaction (closed records gone).
        Assert.True(new FileInfo(path).Length > 0);
    }
}
