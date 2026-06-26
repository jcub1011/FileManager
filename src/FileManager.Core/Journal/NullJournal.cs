namespace FileManager.Core.Journal;

/// <summary>
/// A no-op <see cref="IJournal"/>: records nothing and reports no open Jobs. The default the engine
/// falls back to when no durable journal is wired, so pre-M4 <see cref="FileManager.Core.Jobs.JobEngine"/>
/// constructors and tests behave exactly as before.
/// </summary>
public sealed class NullJournal : IJournal
{
    /// <summary>A shared instance (the type is stateless).</summary>
    public static NullJournal Instance { get; } = new();

    public void Open(JournalRecord open) { }

    public void Record(JournalRecord transition) { }

    public void Close(string jobId) { }

    public IReadOnlyList<OpenJobState> ReadOpenEntries() => Array.Empty<OpenJobState>();

    public void Dispose() { }
}
