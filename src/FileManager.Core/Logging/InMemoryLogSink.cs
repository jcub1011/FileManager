namespace FileManager.Core.Logging;

/// <summary>An <see cref="ILogSink"/> that retains entries in memory for inspection and tests.</summary>
public sealed class InMemoryLogSink : ILogSink
{
    private readonly List<JobLogEntry> _entries = new();

    /// <summary>The entries logged so far, in order.</summary>
    public IReadOnlyList<JobLogEntry> Entries => _entries;

    public void Log(JobLogEntry entry) => _entries.Add(entry);
}
