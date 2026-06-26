namespace FileManager.Core.Audit;

/// <summary>
/// The durable, append-only deletion audit trail (§7): every source disposition and every Mirror
/// deletion is recorded so an operator can later answer "what happened to this file?". Like the
/// journal, each entry is <c>fsync</c>'d so a recorded deletion survives a crash. The default no-op
/// implementation is <see cref="NullAuditLog"/>.
/// </summary>
public interface IAuditLog : IDisposable
{
    /// <summary>Appends one audit entry and flushes it durably.</summary>
    public void Record(AuditEntry entry);
}
