using FileManager.Contracts.Messages;

namespace FileManager.Service.Ipc;

/// <summary>
/// The narrow surface the IPC layer needs from the running engine/host: submit a Payload, snapshot the
/// engine state, list/reload Profiles, and preview (dry-run). Keeping this an interface lets the
/// <see cref="ConnectionDispatcher"/> and <see cref="IpcServer"/> be unit-tested against a fake engine
/// without standing up the full <c>ServiceHost</c>.
/// </summary>
public interface IEngineFacade
{
    /// <summary>Enqueues work for the submitted payload and reports how many Jobs were queued.</summary>
    public SubmitPayloadResult Submit(SubmitPayload payload);

    /// <summary>Returns a point-in-time snapshot of the engine's state.</summary>
    public EngineState GetState();

    /// <summary>Returns summaries of the currently loaded Profiles.</summary>
    public ProfileList ListProfiles();

    /// <summary>Reloads Profiles from disk and reports the new count and any per-file errors.</summary>
    public ReloadResult ReloadProfiles();

    /// <summary>Previews what processing the request would do. M6 returns a shape-only stub.</summary>
    public DryRunReport DryRun(DryRunRequest request);

    /// <summary>
    /// Resolves a pending manual shell invocation (spec §3.2): enqueues the chosen Profile's Jobs for the
    /// pending path, or discards the pending when the user cancelled. Returns the enqueue result (or a
    /// rejection for an unknown/expired id or a cancel).
    /// </summary>
    public SubmitPayloadResult ResolveManualInvocation(ResolveManualInvocation resolution);

    /// <summary>
    /// Snapshots every currently-unresolved manual invocation as a <see cref="ManualInvocationPending"/>.
    /// The IPC layer replays these to a newly-subscribed client so a prompt published BEFORE that client
    /// finished booting + subscribing (e.g. a cold-started GUI) is still delivered — closing the
    /// lost-push race and guaranteeing a manual invocation is never silently dropped.
    /// </summary>
    public IReadOnlyList<ManualInvocationPending> GetUnresolvedManualInvocations();
}
