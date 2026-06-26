using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Logging;
using FileManager.Core.Profiles;
using FileManager.Core.Recovery;
using FileManager.Core.Safety;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// End-to-end journal integration: a successful Job records OPEN…ALL_TARGETS_VERIFIED…CLOSED so it is
/// not left open; a rolled-back Job is closed too; the source-disposition invariant holds; and an
/// engine + recovery round-trip leaves no open entries.
/// </summary>
public sealed class JobEngineJournalTests : IDisposable
{
    private static readonly IngestionContext Ctx =
        new() { Now = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero) };

    private readonly TempDir _temp = new("enginejournal");
    private readonly InMemoryLogSink _log = new();
    private readonly SystemFileOperations _files = new();

    public void Dispose() => _temp.Dispose();

    private JobEngineOptions Options() => new()
    {
        TrashDirectory = _temp.Path("trash"),
        PipelineTempRoot = _temp.Path("pipe"),
        StagingRoot = _temp.Path("staging"),
    };

    private JobEngine BuildEngine(IJournal journal) =>
        new(_files, _log, Options(), processRunner: null, journal: journal, audit: null);

    [Fact]
    public void SuccessfulJob_LeavesNoOpenEntry()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "x");
        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: TestProfiles.DefaultPolicies with { OnSuccess = OnSuccess.PermanentDelete });

        using var journal = new FileJournal(_temp.Path("jobs.journal"));
        JobResult r = BuildEngine(journal).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Closed, r.State);
        Assert.Empty(journal.ReadOpenEntries()); // OPEN…CLOSED, fully resolved.
    }

    [Fact]
    public void ScreenedOutJob_IsClosed()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "x");
        // An exclude filter that rejects the file in Phase 2.
        var filters = new FilterSet { ExcludeGlob = new[] { "*.txt" } };
        Profile p = TestProfiles.Build(new[] { s }, new[] { t }, filters: filters);

        using var journal = new FileJournal(_temp.Path("jobs.journal"));
        JobResult r = BuildEngine(journal).ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Skipped, r.State);
        Assert.Empty(journal.ReadOpenEntries());
    }

    [Fact]
    public void RolledBackJob_IsClosed_SourceIntact()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "x");
        Profile p = TestProfiles.Build(new[] { s }, new[] { t },
            policies: TestProfiles.DefaultPolicies with
            {
                OnSuccess = OnSuccess.PermanentDelete,
                VerificationMethod = VerificationMethod.SHA256,
            });

        using var journal = new FileJournal(_temp.Path("jobs.journal"));
        var corrupting = new CorruptingOps(_files);
        JobResult r = new JobEngine(corrupting, _log, Options(), processRunner: null, journal: journal, audit: null)
            .ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        Assert.True(File.Exists(file));            // source untouched
        Assert.Empty(journal.ReadOpenEntries());   // ROLLED_BACK then CLOSED
    }

    [Fact]
    public void EngineThenRecovery_RoundTrip_NoOpenEntries()
    {
        // A normal run records to a real journal; running recovery afterward is a no-op (nothing open).
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "x");
        Profile p = TestProfiles.Build(new[] { s }, new[] { t });

        using var journal = new FileJournal(_temp.Path("jobs.journal"));
        BuildEngine(journal).ProcessFile(p, file, Ctx);

        var recovery = new RecoveryService(journal, new RollbackEngine(_files), _files, Options());
        RecoveryReport report = recovery.Recover();

        Assert.Equal(0, report.Cleaned);
        Assert.Equal(0, report.RolledBack);
        Assert.Equal(0, report.Errors);
    }

    // A read-corrupting decorator so SHA256 verification fails and the Job rolls back.
    private sealed class CorruptingOps(IFileOperations inner) : IFileOperations
    {
        public Stream OpenRead(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > 0)
                bytes[0] ^= 0xFF;
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
}
