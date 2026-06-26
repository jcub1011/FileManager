using System.IO;
using FileManager.Core.Configuration;
using FileManager.Core.IO;
using FileManager.Core.Safety;

namespace FileManager.Core.Journal;

/// <summary>
/// The file-backed durable <see cref="IJournal"/> (§6.3). Each record is framed
/// (<see cref="JournalFraming"/>) and appended through an <see cref="IDurableAppendWriter"/> with an
/// <c>fsync</c> immediately after — so a record that <see cref="Open"/>/<see cref="Record"/>/
/// <see cref="Close"/> returned from is durable across a crash. <see cref="ReadOpenEntries"/> replays
/// the file (tolerating a torn tail) and folds each Job's records into an <see cref="OpenJobState"/>.
/// </summary>
/// <remarks>
/// <para><b>Rotation / compaction.</b> When the journal grows past a byte threshold, the next write
/// first rewrites the file in place dropping every record that belongs to a Closed Job — the only
/// records recovery still needs are those of still-open Jobs. This keeps the journal bounded under
/// steady-state churn. Compaction closes the append writer, rewrites the file atomically (temp +
/// replace), then reopens. M5 makes every public operation concurrency-safe under a single lock (see
/// <c>_gate</c>), so compaction can no longer race an active append.</para>
/// <para>The journal path resolves from <see cref="ServiceConfig.JournalDirectory"/> (default a
/// <c>journal/</c> folder under <see cref="ConfigPaths.GetConfigDirectory"/>), file name
/// <see cref="DefaultJournalFileName"/>.</para>
/// </remarks>
public sealed class FileJournal : IJournal
{
    /// <summary>Default journal sub-folder under the config directory.</summary>
    public const string DefaultJournalDirName = "journal";

    /// <summary>The journal file name within the journal directory.</summary>
    public const string DefaultJournalFileName = "jobs.journal";

    /// <summary>Default size beyond which the journal is compacted (mirrors the ServiceConfig default).</summary>
    public const long DefaultRotationSizeBytes = ServiceConfig.DefaultJournalRotationSizeBytes;

    private readonly string _path;
    private readonly long _rotationSizeBytes;
    private readonly Func<string, IDurableAppendWriter> _writerFactory;

    // Serializes every append/compaction/read so concurrent worker-pool Jobs (M5) cannot interleave a
    // frame with another's, race compaction against an active append, or read a half-written tail. A
    // single lock is sufficient: the per-record fsync already dominates the cost of a journal write, so
    // the contention here is far cheaper than the durability barrier it guards.
    private readonly object _gate = new();

    private IDurableAppendWriter _writer;

    /// <summary>The resolved journal file path.</summary>
    public string Path => _path;

    /// <summary>
    /// Creates a journal at <paramref name="path"/>. <paramref name="rotationSizeBytes"/> triggers
    /// compaction once the file exceeds it. <paramref name="writerFactory"/> builds the durable
    /// append writer for the path (defaults to <see cref="SystemDurableAppendWriter"/>); tests inject a
    /// fault-injecting factory to simulate torn writes and to count fsyncs.
    /// </summary>
    public FileJournal(
        string path,
        long rotationSizeBytes = DefaultRotationSizeBytes,
        Func<string, IDurableAppendWriter>? writerFactory = null)
    {
        _path = path;
        _rotationSizeBytes = rotationSizeBytes > 0 ? rotationSizeBytes : DefaultRotationSizeBytes;
        _writerFactory = writerFactory ?? (p => new SystemDurableAppendWriter(p));
        _writer = _writerFactory(_path);
    }

    /// <summary>
    /// Resolves a <see cref="FileJournal"/> from <paramref name="config"/>: the file lives under
    /// <see cref="ServiceConfig.JournalDirectory"/> (or the default config-dir location) and uses the
    /// config rotation size. <paramref name="configDirectory"/> overrides the base config dir (tests).
    /// </summary>
    public static FileJournal FromConfig(ServiceConfig config, string? configDirectory = null)
    {
        string dir = config.JournalDirectory
            ?? System.IO.Path.Combine(configDirectory ?? ConfigPaths.GetConfigDirectory(), DefaultJournalDirName);
        string path = System.IO.Path.Combine(dir, DefaultJournalFileName);
        return new FileJournal(path, config.JournalRotationSizeBytes);
    }

    public void Open(JournalRecord open) => Append(open);

    public void Record(JournalRecord transition) => Append(transition);

    public void Close(string jobId)
    {
        Append(new JournalRecord
        {
            SchemaVersion = JournalRecord.CurrentSchemaVersion,
            Event = JournalEventType.Closed,
            JobId = jobId,
            ProfileId = string.Empty,
            SourcePath = string.Empty,
            Timestamp = DateTimeOffset.UtcNow,
        });
    }

    public IReadOnlyList<OpenJobState> ReadOpenEntries()
    {
        // Read under the same lock as writes so a concurrent append/compaction cannot tear the scan.
        lock (_gate)
        {
            List<JournalRecord> records = ReadAll();
            return FoldOpenJobs(records);
        }
    }

    private void Append(JournalRecord record)
    {
        byte[] frame = JournalFraming.Encode(record);
        lock (_gate)
        {
            CompactIfOversized();
            _writer.Append(frame);
            _writer.Flush(); // fsync per record — durability guarantee (§6.3).
        }
    }

    // Reads every well-framed record from the file (tolerating a torn tail / absent file).
    private List<JournalRecord> ReadAll()
    {
        if (!File.Exists(_path))
            return new List<JournalRecord>();

        // FileShare.ReadWrite so the open append writer does not block the recovery read.
        using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return JournalFraming.Decode(stream).ToList();
    }

    // Folds all records into per-Job state, excluding Jobs that have a Closed record.
    private static List<OpenJobState> FoldOpenJobs(List<JournalRecord> records)
    {
        var byJob = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        foreach (JournalRecord r in records)
        {
            if (r.Event == JournalEventType.Closed)
            {
                byJob[r.JobId] = Accumulator.Closed(r.JobId);
                continue;
            }

            if (!byJob.TryGetValue(r.JobId, out Accumulator? acc) || acc.IsClosed)
            {
                // A record after Close is unexpected under the single-writer model; start fresh so the
                // latest open run wins (defensive — Job IDs are unique GUIDs in practice).
                acc = new Accumulator(r.JobId);
                byJob[r.JobId] = acc;
            }

            acc.Apply(r);
        }

        var open = new List<OpenJobState>();
        foreach (Accumulator acc in byJob.Values)
        {
            if (!acc.IsClosed)
                open.Add(acc.ToState());
        }

        return open;
    }

    // Rewrites the file dropping Closed Jobs' records once it exceeds the rotation threshold.
    private void CompactIfOversized()
    {
        long size;
        try
        {
            size = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Can't stat — skip compaction this time; appends still proceed.
        }

        if (size < _rotationSizeBytes)
            return;

        List<JournalRecord> all = ReadAll();
        var closed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JournalRecord r in all)
        {
            if (r.Event == JournalEventType.Closed)
                closed.Add(r.JobId);
        }

        // Keep only records of Jobs that are still open.
        List<JournalRecord> kept = all
            .Where(r => r.Event != JournalEventType.Closed && !closed.Contains(r.JobId))
            .ToList();

        _writer.Dispose();

        string temp = _path + ".compact";
        try
        {
            using (var rewrite = _writerFactory(temp))
            {
                foreach (JournalRecord r in kept)
                    rewrite.Append(JournalFraming.Encode(r));
                rewrite.Flush();
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Compaction is best-effort: if the rewrite fails, leave the original journal intact and
            // just reopen the append writer. Recovery still works against the un-compacted file.
            TryDelete(temp);
        }
        finally
        {
            _writer = _writerFactory(_path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _writer.Dispose();
    }

    /// <summary>Mutable per-Job fold used while replaying the journal.</summary>
    private sealed class Accumulator
    {
        private readonly List<string> _temps = new();
        private readonly List<string> _finals = new();
        private readonly List<RollbackContext.StagedOriginal> _staged = new();

        public string JobId { get; }
        public bool IsClosed { get; private set; }
        private string _profileId = string.Empty;
        private string _sourcePath = string.Empty;
        private JournalEventType _lastEvent = JournalEventType.Open;
        private OnSuccessHolder _disposition;
        private string? _dispositionPath;
        private bool _allVerified;

        public Accumulator(string jobId) => JobId = jobId;

        public static Accumulator Closed(string jobId) => new(jobId) { IsClosed = true };

        public void Apply(JournalRecord r)
        {
            _lastEvent = r.Event;
            if (!string.IsNullOrEmpty(r.ProfileId))
                _profileId = r.ProfileId;
            if (!string.IsNullOrEmpty(r.SourcePath))
                _sourcePath = r.SourcePath;
            if (r.Disposition is { } d)
                _disposition = new OnSuccessHolder(d);
            if (r.DispositionPath is not null)
                _dispositionPath = r.DispositionPath;

            switch (r.Event)
            {
                case JournalEventType.TargetVerified when r.TempPath is not null:
                    _temps.Add(r.TempPath);
                    break;

                case JournalEventType.TargetPlaced when r.FinalPath is not null:
                    // The temp became this final — it is no longer an orphan to delete.
                    if (r.TempPath is not null)
                        _temps.Remove(r.TempPath);
                    _finals.Add(r.FinalPath);
                    break;

                case JournalEventType.TargetStaged when r.StagedPath is not null && r.StagedOriginalPath is not null:
                    _staged.Add(new RollbackContext.StagedOriginal(r.StagedPath, r.StagedOriginalPath));
                    break;

                case JournalEventType.AllTargetsVerified:
                    _allVerified = true;
                    break;
            }
        }

        public OpenJobState ToState() => new()
        {
            JobId = JobId,
            ProfileId = _profileId,
            SourcePath = _sourcePath,
            LastEvent = _lastEvent,
            Disposition = _disposition.Value,
            DispositionPath = _dispositionPath,
            AllTargetsVerified = _allVerified,
            UnpromotedTemps = _temps.ToList(),
            PlacedFinals = _finals.ToList(),
            StagedOriginals = _staged.ToList(),
        };

        // Small struct wrapper so an absent disposition stays null without boxing the enum.
        private readonly struct OnSuccessHolder(Profiles.OnSuccess value)
        {
            public Profiles.OnSuccess? Value { get; } = value;
        }
    }
}
