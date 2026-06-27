namespace FileManager.Contracts.Messages;

/// <summary>
/// A request to enqueue work for a file or directory (spec §2 Shell→Service handoff and GUI submit).
/// Self-contained: it carries only primitives/strings so <see cref="FileManager.Contracts"/> stays
/// dependency-free and never references the engine's <c>Profile</c>/<c>Job</c> types. The service
/// resolves the owning Profile (by <see cref="ProfileId"/> when supplied, else by matching the path to
/// a configured Source) and enqueues a Job per matching file.
/// </summary>
/// <param name="Path">The absolute file or directory path to submit.</param>
/// <param name="ProfileId">
/// Optional Profile id to run under. When null, the service matches the path to whichever Profile owns
/// it via its Sources; when set, only that Profile is considered.
/// </param>
/// <param name="Recursive">
/// When <paramref name="Path"/> is a directory, whether to enumerate it recursively. Ignored for a file
/// path.
/// </param>
/// <param name="IsManual">
/// True when this submission originates from a manual OS shell invocation (spec §3.2 right-click). A
/// manual submission is NEVER auto-enqueued: the service registers a pending invocation and pushes a
/// <see cref="ManualInvocationPending"/> so the user is ALWAYS prompted to pick a Profile (even when one
/// matches), and only the explicitly chosen Profile is enqueued via
/// <see cref="ResolveManualInvocation"/>. Watcher/scheduler/GUI submits leave this false and behave
/// exactly as before. Defaulted so existing positional construction (<c>new(path, id, recursive)</c>)
/// keeps compiling and round-tripping unchanged.
/// </param>
public sealed record SubmitPayload(string Path, string? ProfileId, bool Recursive, bool IsManual = false);

/// <summary>
/// The result of a <see cref="SubmitPayload"/>: whether it was accepted, how many Jobs were queued, and
/// (when refused) a human-readable reason.
/// </summary>
/// <param name="Accepted">True when the path matched a Profile and at least the enqueue was attempted.</param>
/// <param name="QueuedCount">Number of Jobs enqueued onto the engine's queue.</param>
/// <param name="JobIds">The submission ids assigned to the queued Jobs (one per queued file).</param>
/// <param name="Reason">A human-readable explanation when <paramref name="Accepted"/> is false.</param>
/// <param name="PendingInvocationId">
/// Set when a manual submission was accepted-as-pending: the service registered the invocation and is
/// awaiting the user's profile choice (no Jobs queued yet). The shell uses this to know the request did
/// not silently drop and a chooser will appear. Null for ordinary (auto-enqueued) submissions.
/// </param>
public sealed record SubmitPayloadResult(
    bool Accepted,
    int QueuedCount,
    IReadOnlyList<string> JobIds,
    string? Reason,
    string? PendingInvocationId = null)
{
    /// <summary>An accepted result carrying the queued submission ids.</summary>
    public static SubmitPayloadResult Ok(IReadOnlyList<string> jobIds) =>
        new(true, jobIds.Count, jobIds, null);

    /// <summary>A refused result carrying the reason; nothing was queued.</summary>
    public static SubmitPayloadResult Rejected(string reason) =>
        new(false, 0, Array.Empty<string>(), reason);

    /// <summary>
    /// An accepted-but-pending result for a manual invocation: nothing is queued yet; the service has
    /// registered <paramref name="invocationId"/> and pushed a <see cref="ManualInvocationPending"/> so
    /// the user is prompted. The shell treats this as success (the request reached the always-prompt
    /// flow), not a rejection.
    /// </summary>
    public static SubmitPayloadResult Pending(string invocationId) =>
        new(true, 0, Array.Empty<string>(), null, invocationId);
}
