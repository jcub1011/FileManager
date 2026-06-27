using FileManager.Contracts.Messages;

namespace FileManager.Gui.Ipc;

/// <summary>
/// The narrow surface the view-models use to talk to the Service: request/response queries plus a Job
/// event subscription. An interface so view-model tests inject a fake without any transport, and so the
/// real <see cref="IpcClient"/> can be swapped for an offline stub. All methods are async and never throw
/// for an offline service — they surface failure as the caller's choice (timeout / null) rather than
/// crashing the UI.
/// </summary>
public interface IServiceClient
{
    /// <summary>Queries the engine state snapshot, or null if the service is unreachable.</summary>
    public Task<EngineState?> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the loaded Profiles, or null if the service is unreachable.</summary>
    public Task<ProfileList?> ListProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Triggers a Profile reload on the service, or null if it is unreachable.</summary>
    public Task<ReloadResult?> ReloadProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs a dry-run preview, or null if the service is unreachable.</summary>
    public Task<DryRunReport?> DryRunAsync(DryRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to the Job-event stream, invoking <paramref name="onEvent"/> for each pushed
    /// <see cref="JobEvent"/> until <paramref name="cancellationToken"/> fires. Reconnects with backoff
    /// when the service restarts; never throws — connection faults are absorbed and retried.
    /// </summary>
    public Task SubscribeAsync(Action<JobEvent> onEvent, CancellationToken cancellationToken);
}
