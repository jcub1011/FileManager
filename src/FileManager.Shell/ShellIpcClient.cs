using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FileManager.Contracts;

namespace FileManager.Shell;

/// <summary>
/// The shell's minimal IPC client: connects to the current-user endpoint (named pipe on Windows, Unix
/// domain socket on Linux) and returns a bidirectional <see cref="Stream"/> for length-prefixed framed
/// messages. Self-contained in the Shell so the project depends only on <see cref="FileManager.Contracts"/>
/// (the wire DTOs + framing) and not on the engine. A connect failure (service not up) surfaces as a
/// transport exception the <see cref="FallbackLauncher"/> handles by starting the service.
/// </summary>
public static class ShellIpcClient
{
    /// <summary>
    /// Connects to the current-user endpoint within <paramref name="timeout"/> and returns the open
    /// stream. Throws <see cref="TimeoutException"/> / <see cref="SocketException"/> /
    /// <see cref="IOException"/> when the service is not reachable.
    /// </summary>
    public static async Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await ConnectPipeAsync(IpcNames.GetWindowsPipeName(), timeout, cancellationToken)
                .ConfigureAwait(false);

        return await ConnectSocketAsync(IpcNames.GetUnixSocketPath(), timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Stream> ConnectPipeAsync(
        string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        try
        {
            await client.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Stream> ConnectSocketAsync(
        string socketPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        DateTime deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"Unix socket '{socketPath}' was not reachable within {timeout}.");
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
