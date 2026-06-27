using System.Net.Sockets;
using System.Runtime.InteropServices;
using FileManager.Contracts;

namespace FileManager.Service;

/// <summary>
/// Enforces one running service per user, keyed off the per-user IPC endpoint name (spec §2 single
/// instance). The guard is acquired before the host starts accepting:
/// <list type="bullet">
/// <item><b>Windows:</b> a named <see cref="Mutex"/> derived from the pipe name. A second instance fails
/// to acquire it and exits cleanly.</item>
/// <item><b>Linux:</b> probes the Unix socket path — if a live server answers a connect, another
/// instance is running; if the file is stale (connect refused), it is cleared so this instance can bind.</item>
/// </list>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex? _mutex;
    private bool _held;

    private SingleInstanceGuard(Mutex? mutex, bool held)
    {
        _mutex = mutex;
        _held = held;
    }

    /// <summary>True when this process holds the single-instance claim (no other service is running).</summary>
    public bool IsPrimaryInstance => _held;

    /// <summary>
    /// Attempts to claim the single-instance slot for the current user. <paramref name="endpointName"/>
    /// overrides the per-user key (tests pass a unique name); null uses the current-user pipe/socket.
    /// </summary>
    public static SingleInstanceGuard Acquire(string? endpointName = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return AcquireWindows(endpointName ?? IpcNames.GetWindowsPipeName());

        return AcquireUnix(endpointName ?? IpcNames.GetUnixSocketPath());
    }

    // Windows: a Local\ named mutex. The authoritative "another instance exists" signal is
    // createdNew==false (the named object already exists, whether held by another process or another
    // guard in this process); this avoids the per-thread reentrancy of WaitOne (a same-thread second
    // WaitOne would succeed). When we DID create it new, we take ownership so the OS keeps the named
    // object alive for our lifetime and a peer sees createdNew==false.
    private static SingleInstanceGuard AcquireWindows(string pipeName)
    {
        // Mutex names cannot contain '\\'; the pipe name is already a safe slug ("filemanager-<user>").
        string mutexName = $@"Local\FileManager-{pipeName}";
        var mutex = new Mutex(initiallyOwned: false, mutexName, out bool createdNew);

        if (!createdNew)
        {
            // Another guard/instance already owns the named object — we are secondary.
            mutex.Dispose();
            return new SingleInstanceGuard(null, held: false);
        }

        bool held;
        try
        {
            held = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // A previous owner crashed without releasing; we now own it.
            held = true;
        }

        return new SingleInstanceGuard(mutex, held);
    }

    /// <summary>The outcome of probing an existing socket path.</summary>
    private enum SocketProbe
    {
        /// <summary>A connect succeeded — a live server is serving this path (we are secondary).</summary>
        Live,

        /// <summary>Connect was specifically refused — a stale socket from a crashed instance.</summary>
        StaleRefused,

        /// <summary>Connect failed some other way — the path may not be a socket / may be transient.</summary>
        Indeterminate,
    }

    // Linux: a live server answers a connect on the socket path ⇒ secondary. We only treat the path as a
    // stale socket (and delete it) when connect fails with the SPECIFIC "connection refused" error — the
    // signal a real socket file with no listener gives. Any OTHER failure (a non-socket file, a peer
    // mid-restart that created the file but is not yet listening, a permission error) is treated as
    // indeterminate and we do NOT delete, so we never remove an arbitrary file.
    private static SingleInstanceGuard AcquireUnix(string socketPath)
    {
        if (!File.Exists(socketPath))
            return new SingleInstanceGuard(null, held: true);

        switch (Probe(socketPath))
        {
            case SocketProbe.Live:
                return new SingleInstanceGuard(null, held: false); // another instance is serving.

            case SocketProbe.StaleRefused:
                // Confirmed stale socket — clear it so the transport can bind fresh.
                try
                {
                    File.Delete(socketPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new SingleInstanceGuard(null, held: false);
                }

                return new SingleInstanceGuard(null, held: true);

            default:
                // Indeterminate: do not delete (could be a non-socket file or a peer mid-restart). Treat
                // as not-primary so we never race a possibly-live peer or destroy an unrelated file. There
                // is a small residual race (a peer that created the socket file but has not begun
                // listening reads as refused) — but refused specifically means no listener is bound, so a
                // deletion in that window is benign: the peer's bind would have failed on the live file
                // anyway, and our transport recreates the file.
                return new SingleInstanceGuard(null, held: false);
        }
    }

    private static SocketProbe Probe(string socketPath)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return SocketProbe.Live; // something is listening.
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return SocketProbe.StaleRefused; // a real socket with no listener bound.
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
        {
            return SocketProbe.Indeterminate;
        }
    }

    /// <summary>Releases the single-instance claim (Windows mutex). The Linux socket is freed by the transport.</summary>
    public void Dispose()
    {
        if (_mutex is not null)
        {
            if (_held)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Not owned on this thread under an edge case; the dispose below still frees the handle.
                }

                _held = false;
            }

            _mutex.Dispose();
        }
    }
}
