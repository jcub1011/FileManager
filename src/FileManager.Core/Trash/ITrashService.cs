namespace FileManager.Core.Trash;

/// <summary>
/// The outcome of moving a file to the platform trash (§5.3): whether it succeeded, where the file
/// now lives (for the audit trail / logging) when known, and a reason when it did not.
/// </summary>
public sealed record TrashResult(bool Ok, string? TrashedPath, string? Reason)
{
    /// <summary>A successful soft-delete to <paramref name="trashedPath"/> (may be null when the platform hides it).</summary>
    public static TrashResult Success(string? trashedPath) => new(true, trashedPath, null);

    /// <summary>A failed soft-delete carrying <paramref name="reason"/>.</summary>
    public static TrashResult Failure(string reason) => new(false, null, reason);
}

/// <summary>
/// Soft-deletes a file to the platform's native trash so the operation is recoverable (§5.3):
/// the Windows Recycle Bin via <c>IFileOperation</c>, or the FreeDesktop Trash on Linux. Powers both
/// <see cref="FileManager.Core.Profiles.OnSuccess.MoveToTrash"/> source disposition and Mirror surplus
/// removals. Reports a <see cref="TrashResult"/> rather than throwing so callers can fold a failure
/// into a Job failure / rollback decision.
/// </summary>
public interface ITrashService
{
    /// <summary>Moves the file at <paramref name="path"/> to the platform trash.</summary>
    public TrashResult MoveToTrash(string path);
}
