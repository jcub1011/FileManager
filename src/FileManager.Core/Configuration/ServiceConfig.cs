namespace FileManager.Core.Configuration;

/// <summary>
/// Global, service-scoped settings that are deliberately <em>not</em> per-Profile. Stored as
/// <c>config.json</c> in the config directory (sibling to <c>profiles/</c>). Every field has a
/// documented default so an absent or partial file still yields a usable configuration.
/// </summary>
/// <remarks>
/// Consumed by later milestones: <see cref="MaxWorkers"/> in M5, host wiring in M6,
/// <see cref="Allowlist"/> in M9, and the log/journal/audit locations in M4/M7.
/// <para>
/// TODO (Appendix B): the exact log/journal/audit locations and rotation sizes are open items.
/// The defaults below are placeholders finalized alongside their consumers (M4 writer, M7 GUI).
/// </para>
/// </remarks>
public sealed record ServiceConfig
{
    /// <summary>Default rotation size for the persistent log: 10 MiB.</summary>
    public const long DefaultLogRotationSizeBytes = 10L * 1024 * 1024;

    /// <summary>Default rotation size for the deletion audit trail: 50 MiB.</summary>
    public const long DefaultAuditRotationSizeBytes = 50L * 1024 * 1024;

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
    /// Directory for the rotating persistent log. <c>null</c> uses a default under the config
    /// directory (resolved by the M4 log writer). TODO Appendix B.
    /// </summary>
    public string? LogDirectory { get; init; }

    /// <summary>
    /// Directory for the durable Job journal. <c>null</c> uses a default under the config
    /// directory (resolved by the M4 journal). TODO Appendix B.
    /// </summary>
    public string? JournalDirectory { get; init; }

    /// <summary>
    /// File path for the append-only deletion audit trail. <c>null</c> uses a default under the
    /// config directory (resolved by the M4 audit writer). TODO Appendix B.
    /// </summary>
    public string? AuditLogPath { get; init; }

    /// <summary>Rotate the persistent log when it reaches this many bytes.</summary>
    public long LogRotationSizeBytes { get; init; } = DefaultLogRotationSizeBytes;

    /// <summary>Rotate the audit trail when it reaches this many bytes.</summary>
    public long AuditRotationSizeBytes { get; init; } = DefaultAuditRotationSizeBytes;

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
