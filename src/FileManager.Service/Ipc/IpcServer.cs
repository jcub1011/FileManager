using System.Runtime.InteropServices;
using FileManager.Core.Logging;

namespace FileManager.Service.Ipc;

/// <summary>
/// The local-only IPC server (spec §2.1): an accept loop over the per-OS <see cref="IIpcServerTransport"/>
/// (named pipe on Windows, Unix domain socket on Linux) that spawns one detached
/// <see cref="ConnectionDispatcher"/> task per accepted connection. Per-connection faults are isolated —
/// a torn frame, malformed message, or I/O error on one connection is logged and closes only that
/// connection; the accept loop keeps running. No network listener is ever opened.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly IIpcServerTransport _transport;
    private readonly ConnectionDispatcher _dispatcher;
    private readonly ILogSink _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _connectionsGate = new();
    private readonly HashSet<Task> _connections = new();

    private Task? _acceptLoop;

    /// <summary>The bound endpoint description (pipe name / socket path) for diagnostics.</summary>
    public string Endpoint => _transport.Endpoint;

    /// <summary>Creates a server over <paramref name="transport"/> dispatching via <paramref name="dispatcher"/>.</summary>
    public IpcServer(IIpcServerTransport transport, ConnectionDispatcher dispatcher, ILogSink log)
    {
        _transport = transport;
        _dispatcher = dispatcher;
        _log = log;
    }

    /// <summary>
    /// Builds the per-OS server transport for the current user: a Windows named pipe or a Linux Unix
    /// domain socket. <paramref name="endpointOverride"/> lets a test bind a unique endpoint (pipe name
    /// or socket path) so concurrent tests do not collide on the shared per-user endpoint.
    /// </summary>
    public static IIpcServerTransport CreateTransportForCurrentOS(string? endpointOverride = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return endpointOverride is null
                ? NamedPipeServerTransport.ForCurrentUser()
                : new NamedPipeServerTransport(endpointOverride);
        }

        return endpointOverride is null
            ? UnixSocketServerTransport.ForCurrentUser()
            : new UnixSocketServerTransport(endpointOverride);
    }

    /// <summary>Starts the accept loop on a background task. Idempotent — a second call is a no-op.</summary>
    public void Start()
    {
        if (_acceptLoop is not null)
            return;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IIpcConnection connection;
            try
            {
                connection = await _transport.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // shutdown.
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                // A transient accept fault: log and keep accepting (one bad accept never tears down the server).
                Log(LogSeverity.Failure, "IPC_ACCEPT_FAULT", $"Accept failed, continuing: {ex.Message}");
                continue;
            }

            TrackConnection(connection, cancellationToken);
        }
    }

    // Spawns the dispatcher for one connection on a detached task whose faults are caught and logged, so
    // a misbehaving client can never propagate into the accept loop or crash the process.
    private void TrackConnection(IIpcConnection connection, CancellationToken cancellationToken)
    {
        Task handler = Task.Run(async () =>
        {
            try
            {
                await _dispatcher.ServeAsync(connection.Stream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Server shutdown.
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException
                                        or InvalidDataException or ObjectDisposedException)
            {
                Log(LogSeverity.Failure, "IPC_CONN_FAULT", $"Connection closed on fault: {ex.Message}");
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        });

        lock (_connectionsGate)
            _connections.Add(handler);

        // Self-prune the tracking set when the handler completes (keeps it bounded under churn).
        _ = handler.ContinueWith(t =>
        {
            lock (_connectionsGate)
                _connections.Remove(t);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Stops accepting, cancels in-flight connections, and awaits the accept loop and handlers to wind
    /// down. The transport is disposed (releasing/cleaning the endpoint) by <see cref="DisposeAsync"/>.
    /// </summary>
    public async Task StopAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task[] outstanding;
        lock (_connectionsGate)
            outstanding = _connections.ToArray();

        try
        {
            await Task.WhenAll(outstanding).ConfigureAwait(false);
        }
        catch
        {
            // Per-connection faults are already logged in TrackConnection; nothing to add here.
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private void Log(LogSeverity severity, string code, string message) =>
        _log.Log(new JobLogEntry { Severity = severity, Code = code, JobId = string.Empty, Message = message });
}
