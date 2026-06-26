namespace FileManager.Core.Audit;

/// <summary>
/// A no-op <see cref="IAuditLog"/>: records nothing. The default the engine and
/// <see cref="FileManager.Core.Sync.MirrorPlanner"/> fall back to when no audit trail is wired, so
/// pre-M4 call sites behave exactly as before.
/// </summary>
public sealed class NullAuditLog : IAuditLog
{
    /// <summary>A shared instance (the type is stateless).</summary>
    public static NullAuditLog Instance { get; } = new();

    public void Record(AuditEntry entry) { }

    public void Dispose() { }
}
