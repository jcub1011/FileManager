using FileManager.Core.Audit;

namespace FileManager.Core.Tests;

/// <summary>An <see cref="IAuditLog"/> that retains entries in memory for inspection in tests.</summary>
internal sealed class CapturingAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = new();

    public void Record(AuditEntry entry) => Entries.Add(entry);

    public void Dispose() { }
}
