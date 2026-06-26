namespace FileManager.Core.Audit;

/// <summary>
/// One row of the durable deletion audit trail (§7): a file was removed/disposed, what was done to it,
/// where it went (trash/archive destination when applicable), when, and under which Job. Serialized via
/// the source generator; no reflection.
/// </summary>
public sealed record AuditEntry
{
    /// <summary>The path of the file that was disposed/removed.</summary>
    public required string Path { get; init; }

    /// <summary>What was done to it.</summary>
    public required AuditAction Action { get; init; }

    /// <summary>Where it went (trash or archive location), when known; null for an in-place keep or permanent delete.</summary>
    public string? Destination { get; init; }

    /// <summary>When the action occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The Job (or, for a Mirror reconciliation, the Profile) the removal was performed under.</summary>
    public required string JobId { get; init; }
}
