using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FileManager.Contracts;

namespace FileManager.Gui.Ipc;

/// <summary>
/// The production client transport: a Windows named pipe or a Unix domain socket, picking the right one
/// for the current OS and the per-user endpoint name from <see cref="IpcNames"/>. Mirrors the Service's
/// client transport so the GUI dials the exact endpoint the Service serves on. No network listener is
/// opened; the endpoint is local and per-user.
/// </summary>
public sealed class OsIpcClientTransport : IIpcClientTransport
{
    private readonly string? _windowsPipeName;
    private readonly string? _unixSocketPath;

    /// <summary>Creates a transport for the current user's standard endpoint.</summary>
    public OsIpcClientTransport()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            _windowsPipeName = IpcNames.GetWindowsPipeName();
        else
            _unixSocketPath = IpcNames.GetUnixSocketPath();
    }

    /// <summary>
    /// Creates a transport for an explicit endpoint (a bare pipe name on Windows, a socket path
    /// elsewhere). Used by tests to dial an in-process server on a unique endpoint.
    /// </summary>
    public OsIpcClientTransport(string endpoint)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            _windowsPipeName = endpoint;
        else
            _unixSocketPath = endpoint;
    }

    /// <inheritdoc/>
    public async Task<IIpcConnection> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? await ConnectPipeAsync(timeout, cancellationToken).ConfigureAwait(false)
            : await ConnectSocketAsync(timeout, cancellationToken).ConfigureAwait(false);

    [SupportedOSPlatform("windows")]
    private async Task<IIpcConnection> ConnectPipeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".", _windowsPipeName!, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            return new StreamConnection(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IIpcConnection> ConnectSocketAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var endpoint = new UnixDomainSocketEndPoint(_unixSocketPath!);
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return new StreamConnection(new NetworkStream(socket, ownsSocket: true));
            }
            catch (SocketException)
            {
                socket.Dispose();
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"Service socket '{_unixSocketPath}' was not reachable within {timeout}.");
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private sealed class StreamConnection(Stream stream) : IIpcConnection
    {
        public Stream Stream => stream;

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
