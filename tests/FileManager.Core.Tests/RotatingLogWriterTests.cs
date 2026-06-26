using System.IO;
using FileManager.Core.Logging;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// <see cref="RotatingLogWriter"/>: entries land in the file, and the file rotates to a backup once it
/// crosses the size threshold. Verbosity is not re-applied (the engine already filtered).
/// </summary>
public sealed class RotatingLogWriterTests : IDisposable
{
    private readonly TempDir _temp = new("rotlog");

    public void Dispose() => _temp.Dispose();

    private static JobLogEntry Entry(string message) =>
        new() { Severity = LogSeverity.Info, Code = "PLACED", JobId = "job1", Message = message };

    [Fact]
    public void Log_WritesEntriesToFile()
    {
        string path = _temp.Path("logs", "fm.log");
        using (var writer = new RotatingLogWriter(path))
        {
            writer.Log(Entry("first"));
            writer.Log(Entry("second"));
        }

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("first", lines[0]);
        Assert.Contains("PLACED", lines[0]);
        Assert.Contains("job1", lines[0]);
        Assert.Contains("second", lines[1]);
    }

    [Fact]
    public void Log_RotatesWhenSizeThresholdCrossed()
    {
        string path = _temp.Path("logs", "fm.log");
        var clock = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        using (var writer = new RotatingLogWriter(path, rotationSizeBytes: 40, clock: () => clock))
        {
            writer.Log(Entry("an-entry-long-enough-to-exceed-the-forty-byte-threshold"));
            writer.Log(Entry("after-rotation"));
        }

        string dir = _temp.Path("logs");
        string[] backups = Directory.GetFiles(dir, "fm.log.*");
        Assert.NotEmpty(backups);
        Assert.True(File.Exists(path));
        // The fresh active file holds the post-rotation entry.
        Assert.Contains("after-rotation", File.ReadAllText(path));
    }

    [Fact]
    public void Log_DoesNotReapplyVerbosity_PersistsEverythingHanded()
    {
        // The writer is verbosity-agnostic: even an Info entry (which a FailuresOnly profile would have
        // filtered upstream) is persisted, because filtering already happened before Log was called.
        string path = _temp.Path("logs", "fm.log");
        using (var writer = new RotatingLogWriter(path))
            writer.Log(Entry("info-level"));

        Assert.Single(File.ReadAllLines(path));
    }
}
