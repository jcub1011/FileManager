using FileManager.Contracts.Messages;
using FileManager.Core.Logging;

namespace FileManager.Service.Ipc;

/// <summary>
/// Reads framed request messages off one connection, routes each by its <see cref="MessageKind"/> to the
/// <see cref="IEngineFacade"/>, and writes the framed responses back. A <c>Subscribe</c> turns the
/// connection into an event stream: the engine's <see cref="EventBroadcaster"/> pushes
/// <see cref="JobEvent"/>s to it until the peer disconnects. All per-connection faults (a torn frame, a
/// malformed message, an I/O error) are isolated by the caller (<see cref="IpcServer"/>): the dispatcher
/// simply stops on a fault, having never thrown into the accept loop.
/// </summary>
public sealed class ConnectionDispatcher
{
    private readonly IEngineFacade _engine;
    private readonly EventBroadcaster _events;
    private readonly ILogSink _log;

    /// <summary>Creates a dispatcher over <paramref name="engine"/> and the shared event broadcaster.</summary>
    public ConnectionDispatcher(IEngineFacade engine, EventBroadcaster events, ILogSink log)
    {
        _engine = engine;
        _events = events;
        _log = log;
    }

    /// <summary>
    /// Services one connection until the peer closes it or <paramref name="cancellationToken"/> fires.
    /// Each request frame is read, dispatched, and answered; a <c>Subscribe</c> hands the connection to
    /// <see cref="StreamEventsAsync"/>. Returns on clean EOF; throws only on cancellation (the server
    /// catches transport faults around this call).
    /// </summary>
    public async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            byte[]? frame = await Framing.ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false);
            if (frame is null)
                return; // clean EOF — peer closed at a frame boundary.

            if (!ContractsSerializer.TryDeserialize(frame, out IpcMessage? request, out string? error)
                || request is null)
            {
                // A corrupt frame is a per-connection fault: log and close (do not crash the server).
                Log(LogSeverity.Failure, "IPC_BAD_FRAME", $"Discarding malformed IPC frame: {error}");
                return;
            }

            if (request.Kind == MessageKind.Subscribe)
            {
                await StreamEventsAsync(stream, cancellationToken).ConfigureAwait(false);
                return; // a subscription owns the connection for its lifetime.
            }

            IpcMessage? response = Handle(request);
            if (response is not null)
            {
                byte[] payload = ContractsSerializer.SerializeToUtf8Bytes(response);
                await Framing.WriteMessageAsync(stream, payload, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Routes a single request to the engine and produces its response envelope (or null when the kind
    // carries no reply). Unknown kinds are ignored with a log line rather than crashing the connection.
    private IpcMessage? Handle(IpcMessage request) => request.Kind switch
    {
        MessageKind.Submit when request.Submit is { } submit =>
            IpcMessage.ForSubmitResult(_engine.Submit(submit)),

        MessageKind.StateQuery =>
            IpcMessage.ForState(_engine.GetState()),

        MessageKind.ListProfiles =>
            IpcMessage.ForProfileList(_engine.ListProfiles()),

        MessageKind.ReloadProfiles =>
            IpcMessage.ForReloadResult(_engine.ReloadProfiles()),

        MessageKind.DryRun when request.DryRun is { } dryRun =>
            IpcMessage.ForDryRunReport(_engine.DryRun(dryRun)),

        _ => LogAndIgnore(request.Kind),
    };

    private IpcMessage? LogAndIgnore(MessageKind kind)
    {
        Log(LogSeverity.Info, "IPC_UNHANDLED", $"Ignoring unsupported IPC message kind '{kind}'.");
        return null;
    }

    // Drains the connection's subscription onto the wire, pushing each JobEvent as a framed message
    // until the broadcaster completes the reader or the peer disconnects (a write fault ends the loop).
    private async Task StreamEventsAsync(Stream stream, CancellationToken cancellationToken)
    {
        using EventBroadcaster.Subscription subscription = _events.Subscribe();
        try
        {
            await foreach (JobEvent jobEvent in subscription.Reader.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                byte[] payload = ContractsSerializer.SerializeToUtf8Bytes(IpcMessage.ForEvent(jobEvent));
                await Framing.WriteMessageAsync(stream, payload, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutdown — let the connection close.
        }
    }

    private void Log(LogSeverity severity, string code, string message) =>
        _log.Log(new JobLogEntry { Severity = severity, Code = code, JobId = string.Empty, Message = message });
}
