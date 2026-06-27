using FileManager.Contracts.Messages;

namespace FileManager.Gui.Ipc;

/// <summary>
/// The GUI's client to the Core Service over the per-OS IPC endpoint (named pipe / Unix socket). Each
/// request opens a short-lived connection through the injected <see cref="IIpcClientTransport"/>, writes
/// one framed <see cref="IpcMessage"/> via <c>Framing</c> + <c>ContractsSerializer</c>, reads the single
/// framed reply, and closes — a simple, robust request/response model that needs no long-lived
/// connection state. <see cref="SubscribeAsync"/> instead holds one connection open for the event stream
/// and reconnects with backoff when the service restarts. The transport seam makes the whole client
/// testable against an in-process <c>IpcServer</c> on a unique endpoint, with no real process.
/// </summary>
public sealed class IpcClient : IServiceClient
{
    private readonly IIpcClientTransport _transport;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _reconnectDelay;

    /// <summary>Creates a client over <paramref name="transport"/>.</summary>
    /// <param name="transport">The connection seam (production: <see cref="OsIpcClientTransport"/>).</param>
    /// <param name="connectTimeout">Per-attempt connect timeout (defaults to 5s).</param>
    /// <param name="reconnectDelay">Backoff between subscription reconnect attempts (defaults to 1s).</param>
    public IpcClient(IIpcClientTransport transport, TimeSpan? connectTimeout = null, TimeSpan? reconnectDelay = null)
    {
        _transport = transport;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(1);
    }

    /// <inheritdoc/>
    public async Task<EngineState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        IpcMessage? reply = await RequestAsync(IpcMessage.ForStateQuery(), cancellationToken).ConfigureAwait(false);
        return reply?.State;
    }

    /// <inheritdoc/>
    public async Task<ProfileList?> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        IpcMessage? reply = await RequestAsync(IpcMessage.ForListProfiles(), cancellationToken).ConfigureAwait(false);
        return reply?.ProfileList;
    }

    /// <inheritdoc/>
    public async Task<ReloadResult?> ReloadProfilesAsync(CancellationToken cancellationToken = default)
    {
        IpcMessage? reply = await RequestAsync(IpcMessage.ForReloadProfiles(), cancellationToken).ConfigureAwait(false);
        return reply?.ReloadResult;
    }

    /// <inheritdoc/>
    public async Task<DryRunReport?> DryRunAsync(DryRunRequest request, CancellationToken cancellationToken = default)
    {
        IpcMessage? reply = await RequestAsync(IpcMessage.ForDryRun(request), cancellationToken).ConfigureAwait(false);
        return reply?.DryRunReport;
    }

    /// <inheritdoc/>
    public async Task SubscribeAsync(Action<JobEvent> onEvent, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using IIpcConnection connection =
                    await _transport.ConnectAsync(_connectTimeout, cancellationToken).ConfigureAwait(false);

                byte[] subscribe = ContractsSerializer.SerializeToUtf8Bytes(IpcMessage.ForSubscribe());
                await Framing.WriteMessageAsync(connection.Stream, subscribe, cancellationToken).ConfigureAwait(false);

                while (!cancellationToken.IsCancellationRequested)
                {
                    byte[]? frame = await Framing.ReadMessageAsync(connection.Stream, cancellationToken)
                        .ConfigureAwait(false);
                    if (frame is null)
                        break; // service closed at a frame boundary — fall through to reconnect.

                    if (ContractsSerializer.TryDeserialize(frame, out IpcMessage? message, out _)
                        && message is { Kind: MessageKind.Event, Event: { } jobEvent })
                    {
                        onEvent(jobEvent);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return; // graceful shutdown.
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException)
            {
                // Service down / restarting / torn frame: back off and reconnect.
            }

            await DelaySafely(_reconnectDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    // One request/response round-trip over a fresh connection. Returns null when the service is
    // unreachable or the reply is malformed, so a transient outage degrades the UI rather than crashing.
    private async Task<IpcMessage?> RequestAsync(IpcMessage request, CancellationToken cancellationToken)
    {
        try
        {
            await using IIpcConnection connection =
                await _transport.ConnectAsync(_connectTimeout, cancellationToken).ConfigureAwait(false);

            byte[] payload = ContractsSerializer.SerializeToUtf8Bytes(request);
            await Framing.WriteMessageAsync(connection.Stream, payload, cancellationToken).ConfigureAwait(false);

            byte[]? frame = await Framing.ReadMessageAsync(connection.Stream, cancellationToken).ConfigureAwait(false);
            if (frame is null)
                return null;

            return ContractsSerializer.TryDeserialize(frame, out IpcMessage? message, out _) ? message : null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException)
        {
            return null;
        }
    }

    private static async Task DelaySafely(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
