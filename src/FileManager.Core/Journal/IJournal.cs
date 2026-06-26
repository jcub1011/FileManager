namespace FileManager.Core.Journal;

/// <summary>
/// The durable, append-only Job journal (§6.3). The engine records a Job's lifecycle transitions here
/// with an <c>fsync</c> per record so that, after a crash, a <see cref="FileManager.Core.Recovery.RecoveryService"/>
/// can scan for Jobs left OPEN (never <see cref="JournalEventType.Closed"/>) and either clean or roll
/// them back. The headline guarantee: a source is never disposed unless the journal durably recorded
/// <see cref="JournalEventType.AllTargetsVerified"/> for that Job.
/// </summary>
/// <remarks>
/// Single-writer in M4 (one Job runs at a time); M5 makes journal access concurrency-safe.
/// The default no-op implementation is <see cref="NullJournal"/>, so existing engine constructors and
/// tests are unaffected.
/// </remarks>
public interface IJournal : IDisposable
{
    /// <summary>Records the opening <see cref="JournalEventType.Open"/> transition for a Job.</summary>
    public void Open(JournalRecord open);

    /// <summary>Records a mid-life transition for an already-open Job.</summary>
    public void Record(JournalRecord transition);

    /// <summary>
    /// Records the terminal <see cref="JournalEventType.Closed"/> marker for <paramref name="jobId"/>,
    /// excluding it from future <see cref="ReadOpenEntries"/> scans.
    /// </summary>
    public void Close(string jobId);

    /// <summary>
    /// Reconstructs, per Job ID, the state of every Job that was opened but not closed: the furthest
    /// event reached plus accumulated artifacts. Jobs with a <see cref="JournalEventType.Closed"/>
    /// record are excluded. A torn tail record is skipped cleanly (never fatal).
    /// </summary>
    public IReadOnlyList<OpenJobState> ReadOpenEntries();
}
