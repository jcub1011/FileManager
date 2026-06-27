using System.IO;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace FileManager.Service.Ipc;

/// <summary>
/// The Linux IPC transport over a Unix domain socket (spec §2.1). The socket file is bound at the
/// per-user path and its permissions are set to <c>0600</c> (owner read/write only) on creation — the §9
/// least-privilege requirement — and the socket file is removed on shutdown and on stale detection. No
/// network listener is opened; <see cref="AddressFamily.Unix"/> is filesystem-scoped.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixSocketServerTransport : IIpcServerTransport
{
    private readonly string _socketPath;
    private readonly Socket _listener;

    /// <summary>
    /// Binds a listening Unix socket at <paramref name="socketPath"/>, creating the parent directory
    /// (owner-only) and the socket file (0600), and begins listening with the given backlog.
    /// </summary>
    public UnixSocketServerTransport(string socketPath, int backlog = 16)
    {
        _socketPath = socketPath;

        string? dir = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            TrySetOwnerOnlyDirectory(dir);
        }

        // A leftover socket file from a previous (crashed) instance must be cleared before bind, or
        // bind fails with "address already in use". SingleInstanceGuard has already confirmed no live
        // server answers this path, so removing the stale file here is safe.
        TryDeleteSocketFile(_socketPath);

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        // The 0700 parent directory above is the PRIMARY protection. As defense-in-depth, close the
        // TOCTOU window between bind() (which creates the socket file under the process umask) and the
        // explicit chmod below by forcing an owner-only umask (0077) across the bind, so the file is
        // never even briefly group/other-accessible (milestone Risk: "verify permissions on creation").
        var endpoint = new UnixDomainSocketEndPoint(_socketPath);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            UnixInterop.WithOwnerOnlyUmask(() => _listener.Bind(endpoint));
        else
            _listener.Bind(endpoint);

        TrySetOwnerOnlyFile(_socketPath);
        _listener.Listen(backlog);
    }

    /// <summary>Creates a server transport on the current user's socket (<see cref="FileManager.Contracts.IpcNames.GetUnixSocketPath()"/>).</summary>
    public static UnixSocketServerTransport ForCurrentUser() =>
        new(FileManager.Contracts.IpcNames.GetUnixSocketPath());

    /// <inheritdoc/>
    public string Endpoint => _socketPath;

    /// <inheritdoc/>
    public async Task<IIpcConnection> AcceptAsync(CancellationToken cancellationToken)
    {
        Socket accepted = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        return new SocketConnection(accepted);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _listener.Dispose();
        // Clean up the bound socket file so the path is free for the next start (and not left as a
        // stale file another instance must detect/clear).
        TryDeleteSocketFile(_socketPath);
        return ValueTask.CompletedTask;
    }

    private static void TrySetOwnerOnlyFile(string path)
    {
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best-effort hardening: a filesystem that does not honor Unix modes still works; the bind
            // path already lives under a per-user runtime/temp directory.
        }
    }

    private static void TrySetOwnerOnlyDirectory(string path)
    {
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

    private static void TryDeleteSocketFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class SocketConnection : IIpcConnection
    {
        private readonly Socket _socket;
        private readonly NetworkStream _stream;

        public SocketConnection(Socket socket)
        {
            _socket = socket;
            _stream = new NetworkStream(socket, ownsSocket: false);
        }

        public Stream Stream => _stream;

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // Peer already gone; shutdown is best-effort.
            }

            _socket.Dispose();
        }
    }
}

/// <summary>
/// The Linux IPC client transport over a Unix domain socket. Connects to the per-user socket path,
/// retrying within the supplied timeout so a just-started service is reached.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixSocketClientTransport : IIpcClientTransport
{
    private readonly string _socketPath;

    /// <summary>Creates a client transport for the socket at <paramref name="socketPath"/>.</summary>
    public UnixSocketClientTransport(string socketPath) => _socketPath = socketPath;

    /// <summary>Creates a client transport for the current user's socket.</summary>
    public static UnixSocketClientTransport ForCurrentUser() =>
        new(FileManager.Contracts.IpcNames.GetUnixSocketPath());

    /// <inheritdoc/>
    public async Task<IIpcConnection> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var endpoint = new UnixDomainSocketEndPoint(_socketPath);
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return new SocketConnection(socket);
            }
            catch (SocketException)
            {
                // Server not up yet (no socket file / nothing listening). Retry until the deadline.
                socket.Dispose();
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"Unix socket '{_socketPath}' was not reachable within {timeout}.");

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private sealed class SocketConnection : IIpcConnection
    {
        private readonly Socket _socket;
        private readonly NetworkStream _stream;

        public SocketConnection(Socket socket)
        {
            _socket = socket;
            _stream = new NetworkStream(socket, ownsSocket: false);
        }

        public Stream Stream => _stream;

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }

            _socket.Dispose();
        }
    }
}
