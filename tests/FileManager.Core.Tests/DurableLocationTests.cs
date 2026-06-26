using System.IO;
using FileManager.Core.Audit;
using FileManager.Core.Configuration;
using FileManager.Core.Journal;
using FileManager.Core.Logging;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Resolution of the M4 durable-file locations from <see cref="ServiceConfig"/>: a configured path is
/// honored verbatim, and an absent one falls back to the documented default under the config dir.
/// </summary>
public sealed class DurableLocationTests : IDisposable
{
    private readonly TempDir _temp = new("locations");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Journal_DefaultsUnderConfigDir_WhenUnset()
    {
        using var journal = FileJournal.FromConfig(new ServiceConfig(), configDirectory: _temp.Root);

        string expected = Path.Combine(_temp.Root, FileJournal.DefaultJournalDirName, FileJournal.DefaultJournalFileName);
        Assert.Equal(expected, journal.Path);
    }

    [Fact]
    public void Journal_HonorsConfiguredDirectory()
    {
        var config = new ServiceConfig { JournalDirectory = _temp.Path("custom-journal") };
        using var journal = FileJournal.FromConfig(config, configDirectory: _temp.Root);

        Assert.Equal(Path.Combine(_temp.Path("custom-journal"), FileJournal.DefaultJournalFileName), journal.Path);
    }

    [Fact]
    public void Journal_FromConfig_HonorsConfiguredRotationSize()
    {
        // A tiny configured rotation size must reach the journal (not be ignored for the 8 MiB default):
        // with a 64-byte threshold, a closed Job is dropped by compaction on the next append.
        var config = new ServiceConfig { JournalDirectory = _temp.Root, JournalRotationSizeBytes = 64 };
        using var journal = FileJournal.FromConfig(config, configDirectory: _temp.Root);

        var open = new JournalRecord
        {
            SchemaVersion = JournalRecord.CurrentSchemaVersion,
            Event = JournalEventType.Open,
            JobId = "closed",
            ProfileId = "p",
            SourcePath = @"C:\src\a.txt",
            Timestamp = DateTimeOffset.UnixEpoch,
        };
        journal.Open(open);
        journal.Record(open with { Event = JournalEventType.AllTargetsVerified });
        journal.Close("closed");
        journal.Open(open with { JobId = "survivor" }); // file now over 64 bytes ⇒ compaction runs

        OpenJobState job = Assert.Single(journal.ReadOpenEntries());
        Assert.Equal("survivor", job.JobId);
    }

    [Fact]
    public void Audit_DefaultsUnderConfigDir_WhenUnset()
    {
        using var audit = AuditLog.FromConfig(new ServiceConfig(), configDirectory: _temp.Root);

        Assert.Equal(Path.Combine(_temp.Root, AuditLog.DefaultAuditFileName), audit.Path);
    }

    [Fact]
    public void Audit_HonorsConfiguredPath()
    {
        var config = new ServiceConfig { AuditLogPath = _temp.Path("custom", "deletions.log") };
        using var audit = AuditLog.FromConfig(config, configDirectory: _temp.Root);

        Assert.Equal(_temp.Path("custom", "deletions.log"), audit.Path);
    }

    [Fact]
    public void Log_DefaultsUnderConfigDir_WhenUnset()
    {
        using var log = RotatingLogWriter.FromConfig(new ServiceConfig(), configDirectory: _temp.Root);

        string expected = Path.Combine(_temp.Root, RotatingLogWriter.DefaultLogDirName, RotatingLogWriter.DefaultLogFileName);
        Assert.Equal(expected, log.Path);
    }

    [Fact]
    public void Log_HonorsConfiguredDirectory()
    {
        var config = new ServiceConfig { LogDirectory = _temp.Path("custom-logs") };
        using var log = RotatingLogWriter.FromConfig(config, configDirectory: _temp.Root);

        Assert.Equal(Path.Combine(_temp.Path("custom-logs"), RotatingLogWriter.DefaultLogFileName), log.Path);
    }
}
