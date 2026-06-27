using System.IO;

namespace FileManager.Gui.Ipc;

/// <summary>
/// One open client connection to the Service: a bidirectional, length-prefixed-framed byte stream the
/// <see cref="IpcClient"/> reads/writes through <c>Framing</c>. Disposing closes the underlying pipe /
/// socket. Kept as an abstraction so tests can connect to an in-process <c>IpcServer</c> over a real
/// loopback transport without spawning a separate process.
/// </summary>
public interface IIpcConnection : IAsyncDisposable
{
    /// <summary>The framed byte stream for this connection.</summary>
    public Stream Stream { get; }
}

/// <summary>
/// Connects a client to the Service's per-user endpoint. The single seam that makes the
/// <see cref="IpcClient"/> testable: production uses <see cref="OsIpcClientTransport"/> (named pipe /
/// Unix socket); tests pass a transport that dials an in-process server on a unique endpoint.
/// </summary>
public interface IIpcClientTransport
{
    /// <summary>
    /// Opens a new connection, retrying within <paramref name="timeout"/> for the service to come up.
    /// Throws <see cref="TimeoutException"/> if it is unreachable, or
    /// <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    public Task<IIpcConnection> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
