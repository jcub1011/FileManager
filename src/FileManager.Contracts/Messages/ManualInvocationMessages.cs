namespace FileManager.Contracts.Messages;

/// <summary>
/// A service→client push announcing that a manual OS shell invocation (spec §3.2 right-click) is waiting
/// for the user to choose a Profile. The service NEVER auto-runs a manual invocation: it registers the
/// pending invocation, computes ALL Profiles whose Sources own the path, and broadcasts this so a
/// subscribed GUI raises the always-prompt chooser. The client answers with a
/// <see cref="ResolveManualInvocation"/> carrying the same <see cref="InvocationId"/>.
/// </summary>
/// <param name="InvocationId">The opaque id correlating this prompt with its <see cref="ResolveManualInvocation"/>.</param>
/// <param name="Path">The absolute file or directory path the user invoked on.</param>
/// <param name="Recursive">Whether a directory invocation should descend recursively (honoring MaxDepth at execution).</param>
/// <param name="Matches">
/// Every Profile whose configured Source owns the path (may be empty — the chooser still appears so the
/// user can pick "Create Profile…", §3.2, and is never a dead end). Reuses <see cref="ProfileSummary"/>.
/// </param>
public sealed record ManualInvocationPending(
    string InvocationId,
    string Path,
    bool Recursive,
    IReadOnlyList<ProfileSummary> Matches);

/// <summary>
/// A client→service answer to a <see cref="ManualInvocationPending"/>: the user's choice for the pending
/// manual invocation. A non-null <see cref="ChosenProfileId"/> enqueues that Profile's Jobs for the
/// pending path; a null <see cref="ChosenProfileId"/> cancels and discards the pending invocation.
/// </summary>
/// <param name="InvocationId">The id from the matching <see cref="ManualInvocationPending"/>.</param>
/// <param name="ChosenProfileId">The chosen Profile id, or null to cancel.</param>
public sealed record ResolveManualInvocation(string InvocationId, string? ChosenProfileId);
