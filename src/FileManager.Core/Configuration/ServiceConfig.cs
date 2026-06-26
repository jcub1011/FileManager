namespace FileManager.Core.Configuration;

/// <summary>
/// Global, service-scoped settings that are deliberately <em>not</em> per-Profile. Stored as
/// <c>config.json</c> in the config directory (sibling to <c>profiles/</c>). Every field has a
/// documented default so an absent or partial file still yields a usable configuration.
/// </summary>
/// <remarks>
/// Consumed by later milestones: <see cref="MaxWorkers"/> in M5, host wiring in M6,
/// <see cref="Allowlist"/> in M9. The log/journal/audit locations are consumed by M4.
/// <para>
/// Appendix B (M4-finalized) durable-file locations — all relative to
/// <see cref="ConfigPaths.GetConfigDirectory"/> when the corresponding field is null:
/// <list type="bullet">
/// <item><see cref="JournalDirectory"/> → <c>journal/jobs.journal</c> (rotated/compacted in place).</item>
/// <item><see cref="AuditLogPath"/> → <c>deletions.audit</c> (rotated to a timestamped backup).</item>
/// <item><see cref="LogDirectory"/> → <c>logs/filemanager.log</c> (rotated to a timestamped backup).</item>
/// </list>
/// Journal/audit records are framed (length + CRC-32) and fsync'd per record; the application log is a
/// best-effort rotating text log.
/// </para>
/// </remarks>
public sealed record ServiceConfig
{
    /// <summary>Default rotation size for the persistent log: 10 MiB.</summary>
    public const long DefaultLogRotationSizeBytes = 10L * 1024 * 1024;

    /// <summary>Default rotation size for the deletion audit trail: 50 MiB.</summary>
    public const long DefaultAuditRotationSizeBytes = 50L * 1024 * 1024;

    /// <summary>Default compaction threshold for the durable Job journal: 8 MiB.</summary>
    public const long DefaultJournalRotationSizeBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Size of the bounded worker pool (§5.4). Defaults to the machine's logical CPU count.
    /// </summary>
    public int MaxWorkers { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Optional executable allowlist (§9). <c>null</c> means no restriction; a non-null list
    /// restricts which executables Profiles may invoke. M9 enforces this.
    /// </summary>
    public IReadOnlyList<string>? Allowlist { get; init; }

    /// <summary>
    /// Directory for the rotating persistent application log. <c>null</c> resolves to
    /// <c>&lt;config&gt;/logs/</c> (file <c>filemanager.log</c>), via
    /// <see cref="FileManager.Core.Logging.RotatingLogWriter.FromConfig"/>.
    /// </summary>
    public string? LogDirectory { get; init; }

    /// <summary>
    /// Directory for the durable Job journal. <c>null</c> resolves to <c>&lt;config&gt;/journal/</c>
    /// (file <c>jobs.journal</c>), via <see cref="FileManager.Core.Journal.FileJournal.FromConfig"/>.
    /// </summary>
    public string? JournalDirectory { get; init; }

    /// <summary>
    /// File path for the append-only deletion audit trail. <c>null</c> resolves to
    /// <c>&lt;config&gt;/deletions.audit</c>, via
    /// <see cref="FileManager.Core.Audit.AuditLog.FromConfig"/>.
    /// </summary>
    public string? AuditLogPath { get; init; }

    /// <summary>Rotate the persistent log when it reaches this many bytes.</summary>
    public long LogRotationSizeBytes { get; init; } = DefaultLogRotationSizeBytes;

    /// <summary>Rotate the audit trail when it reaches this many bytes.</summary>
    public long AuditRotationSizeBytes { get; init; } = DefaultAuditRotationSizeBytes;

    /// <summary>Compact the Job journal (dropping closed-Job records) when it reaches this many bytes.</summary>
    public long JournalRotationSizeBytes { get; init; } = DefaultJournalRotationSizeBytes;

    /// <summary>
    /// Headroom (in bytes) the proactive disk-space checks keep free on every volume: a Target write
    /// or trash move is refused unless <c>available - reserved - margin</c> still covers it (§3.3
    /// data-safety). Defaults to <c>0</c> so the engine refuses only when a volume genuinely cannot fit
    /// the bytes — a non-zero margin is strictly opt-in, avoiding surprising failures on volumes that
    /// merely run close to full. Threaded into the engine's <see cref="SpaceReservationLedger"/> and
    /// the trash free-space checks.
    /// </summary>
    public long MinFreeSpaceMarginBytes { get; init; }
}
