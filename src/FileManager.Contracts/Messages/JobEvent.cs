namespace FileManager.Contracts.Messages;

/// <summary>
/// A request to subscribe to the engine's Job-event stream. After the service receives this on a
/// connection, it pushes <see cref="JobEvent"/> messages on that same connection until it closes. Carries
/// no fields; its envelope discriminator identifies it.
/// </summary>
public sealed record SubscribeEvents;

/// <summary>
/// One activity/Job event pushed to subscribers (spec §2 event stream). Self-contained strings only so
/// <see cref="FileManager.Contracts"/> stays dependency-free; the engine's enum states are projected to
/// their string names.
/// </summary>
/// <param name="JobId">The submission/Job id this event belongs to.</param>
/// <param name="ProfileId">The Profile the Job ran under.</param>
/// <param name="State">The Job's terminal/transition state as a string (e.g. <c>Closed</c>, <c>Failed</c>).</param>
/// <param name="Code">A short stable code for the event (e.g. <c>QUEUED</c>, <c>COMPLETED</c>).</param>
/// <param name="Message">A human-readable description.</param>
/// <param name="Timestamp">When the event occurred (UTC).</param>
public sealed record JobEvent(
    string JobId,
    string ProfileId,
    string State,
    string Code,
    string Message,
    DateTimeOffset Timestamp);
