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
}
