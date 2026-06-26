using System.IO;
using FileManager.Core.Audit;
using FileManager.Core.Journal;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Verifies the M5 concurrency-safety of the M4 durable writers: under many concurrent writers every
/// record/entry is present, framed correctly, and parseable (no torn/interleaved frames).
/// </summary>
public sealed class DurableConcurrencyTests
{
    [Fact]
    public void FileJournal_ConcurrentWriters_AllRecordsPresentAndParseable()
    {
        using var dir = new TempDir("journal-conc");
        string path = Path.Combine(dir.Root, "jobs.journal");
        using var journal = new FileJournal(path);

        const int writers = 8;
        const int perWriter = 50;

        Parallel.For(0, writers, w =>
        {
            for (int i = 0; i < perWriter; i++)
            {
                string jobId = $"job-{w}-{i}";
                journal.Open(new JournalRecord
                {
                    SchemaVersion = JournalRecord.CurrentSchemaVersion,
                    Event = JournalEventType.Open,
                    JobId = jobId,
                    ProfileId = "p",
                    SourcePath = $"/src/{jobId}",
                    Timestamp = DateTimeOffset.UtcNow,
                });
                journal.Record(new JournalRecord
                {
                    SchemaVersion = JournalRecord.CurrentSchemaVersion,
                    Event = JournalEventType.Screened,
                    JobId = jobId,
                    ProfileId = "p",
                    SourcePath = $"/src/{jobId}",
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }
        });

        // Every opened Job (none closed) must reconstruct cleanly — proves no frame was torn/interleaved.
        IReadOnlyList<OpenJobState> open = journal.ReadOpenEntries();
        Assert.Equal(writers * perWriter, open.Count);
    }

    [Fact]
    public void AuditLog_ConcurrentWriters_AllEntriesPresentAndParseable()
    {
        using var dir = new TempDir("audit-conc");
        string path = Path.Combine(dir.Root, "deletions.audit");
        using (var audit = new AuditLog(path))
        {
            const int writers = 8;
            const int perWriter = 50;

            Parallel.For(0, writers, w =>
            {
                for (int i = 0; i < perWriter; i++)
                {
                    audit.Record(new AuditEntry
                    {
                        Path = $"/src/{w}-{i}",
                        Action = AuditAction.PermanentDelete,
                        Timestamp = DateTimeOffset.UtcNow,
                        JobId = $"job-{w}-{i}",
                    });
                }
            });
        }

        // Each entry is one JSON line; every line must parse back to an AuditEntry.
        string[] lines = File.ReadAllLines(path);
        Assert.Equal(8 * 50, lines.Length);
        foreach (string line in lines)
        {
            Assert.True(ProfileSerializer.TryDeserializeAuditEntry(line, out AuditEntry? entry, out string? err), err);
            Assert.NotNull(entry);
        }
    }
}
