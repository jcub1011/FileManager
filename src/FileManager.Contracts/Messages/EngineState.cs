namespace FileManager.Contracts.Messages;

/// <summary>
/// A request for a snapshot of the engine's current state (queue depth, in-flight Jobs, worker count,
/// per-state tallies, loaded Profile count). Carries no fields — it is a pure query whose discriminator
/// in the envelope identifies it.
/// </summary>
public sealed record EngineStateQuery;

/// <summary>
/// A point-in-time snapshot of the running engine (spec §2 state query). Counts are best-effort and
/// observed without stopping the engine, so they are consistent enough for a GUI dashboard but not a
/// transactional guarantee.
/// </summary>
/// <param name="QueuedCount">Jobs accepted onto the queue but not yet started.</param>
/// <param name="InFlightCount">Jobs currently being processed by a worker.</param>
/// <param name="WorkerCount">The configured worker-pool size (max concurrent Jobs).</param>
/// <param name="ProfileCount">Number of valid Profiles loaded by the service.</param>
/// <param name="ClosedCount">Jobs that have completed successfully since the service started.</param>
/// <param name="SkippedCount">Jobs that were screened out since the service started.</param>
/// <param name="FailedCount">Jobs that failed since the service started.</param>
public sealed record EngineState(
    int QueuedCount,
    int InFlightCount,
    int WorkerCount,
    int ProfileCount,
    long ClosedCount,
    long SkippedCount,
    long FailedCount);
