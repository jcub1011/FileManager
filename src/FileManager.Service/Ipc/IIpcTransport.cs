namespace FileManager.Service.Ipc;

/// <summary>
/// One accepted client connection: a bidirectional, length-prefixed-framed byte stream plus the
/// per-connection lifetime. The <see cref="IpcServer"/> reads request frames and writes response/event
/// frames over <see cref="Stream"/> (via <c>FileManager.Contracts.Messages.Framing</c>) until the peer
/// disconnects or the connection is disposed.
/// </summary>
public interface IIpcConnection : IAsyncDisposable
{
    /// <summary>The bidirectional stream carrying framed messages for this connection.</summary>
    public Stream Stream { get; }
}

/// <summary>
/// The per-OS server transport seam (spec §2.1): a named pipe on Windows, a Unix domain socket on Linux.
/// It is local-only — no network listener is ever opened — and is scoped to the current user via the OS
/// ACL/permission set on creation (§9 least privilege). <see cref="AcceptAsync"/> yields one accepted
/// <see cref="IIpcConnection"/> at a time; the server runs an accept loop over it.
/// </summary>
public interface IIpcServerTransport : IAsyncDisposable
{
    /// <summary>A human-readable description of the bound endpoint (pipe name / socket path).</summary>
    public string Endpoint { get; }

    /// <summary>
    /// Awaits and returns the next inbound connection. Throws <see cref="OperationCanceledException"/>
    /// when <paramref name="cancellationToken"/> fires (server shutdown). May throw a transport
    /// exception on a transient accept fault, which the server logs and recovers from.
    /// </summary>
    public Task<IIpcConnection> AcceptAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The per-OS client transport seam: connects to the current-user endpoint and returns a framed
/// connection. Used by the shell fallback launcher and by tests exercising a real round-trip.
/// </summary>
public interface IIpcClientTransport
{
    /// <summary>
    /// Connects to the endpoint, retrying within <paramref name="timeout"/> for the server to come up.
    /// Returns the connection on success. Throws <see cref="TimeoutException"/> if the server never
    /// became reachable within the timeout, or <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    public Task<IIpcConnection> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
