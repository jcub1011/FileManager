using System.IO;
using FileManager.Core.Audit;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// <see cref="AuditLog"/> durability and rotation: entries are fsync'd per write, land in the file as
/// readable JSON lines, and the file rotates to a backup once it crosses the size threshold.
/// </summary>
public sealed class AuditLogTests : IDisposable
{
    private readonly TempDir _temp = new("auditlog");

    public void Dispose() => _temp.Dispose();

    private static AuditEntry Entry(string path) => new()
    {
        Path = path,
        Action = AuditAction.PermanentDelete,
        Destination = null,
        Timestamp = DateTimeOffset.UnixEpoch,
        JobId = "job1",
    };

    [Fact]
    public void Record_FsyncsPerEntry()
    {
        var writer = new FaultInjectingDurableWriter();
        var audit = new AuditLog(_temp.Path("a.audit"), writerFactory: _ => writer);

        audit.Record(Entry(@"C:\a.txt"));
        audit.Record(Entry(@"C:\b.txt"));

        Assert.Equal(2, writer.FlushCount);
    }

    [Fact]
    public void Record_WritesReadableJsonLines()
    {
        string path = _temp.Path("a.audit");
        using (var audit = new AuditLog(path))
        {
            audit.Record(Entry(@"C:\a.txt"));
            audit.Record(Entry(@"C:\b.txt") with { Action = AuditAction.MoveToTrash, Destination = @"C:\trash\b.txt" });
        }

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.True(ProfileSerializer.TryDeserializeAuditEntry(lines[0], out AuditEntry? e0, out _));
        Assert.Equal(AuditAction.PermanentDelete, e0!.Action);
        Assert.True(ProfileSerializer.TryDeserializeAuditEntry(lines[1], out AuditEntry? e1, out _));
        Assert.Equal(AuditAction.MoveToTrash, e1!.Action);
        Assert.Equal(@"C:\trash\b.txt", e1.Destination);
    }

    [Fact]
    public void Rotation_MovesToBackup_WhenOversized()
    {
        string path = _temp.Path("a.audit");
        // A small threshold forces rotation; a fixed clock makes the backup name deterministic.
        var clock = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        using var audit = new AuditLog(path, rotationSizeBytes: 32, clock: () => clock);

        audit.Record(Entry(@"C:\first-entry-that-exceeds-the-tiny-threshold.txt"));
        audit.Record(Entry(@"C:\second.txt")); // by now the file is over 32 bytes ⇒ rotates first

        // A backup file exists alongside the fresh active file.
        string dir = Path.GetDirectoryName(path)!;
        string[] backups = Directory.GetFiles(dir, "a.audit.*");
        Assert.NotEmpty(backups);
        Assert.True(File.Exists(path)); // active file recreated
    }
}
