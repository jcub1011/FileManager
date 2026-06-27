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

    /// <summary>The outcome of probing an existing path at the socket endpoint.</summary>
    private enum SocketProbe
    {
        /// <summary>A live server answered a connect — another instance is serving (we are secondary).</summary>
        Live,

        /// <summary>Confirmed a Unix socket with no listener (a crashed instance) — safe to clear.</summary>
        StaleSocket,

        /// <summary>The path is not a socket, or the state is otherwise unclear — must NOT be deleted.</summary>
        Indeterminate,
    }

    // Linux: decide whether the existing endpoint path is a live server, a stale socket we may clear, or
    // something we must leave alone. CRITICAL: on real Linux connect() returns ECONNREFUSED for BOTH a
    // stale socket AND a regular (non-socket) file, so connection-refused alone CANNOT distinguish them.
    // We therefore classify the FILE TYPE first (open-as-file), and only run the liveness probe once the
    // path is confirmed to be a socket — so a non-socket file is never deleted.
    private static SingleInstanceGuard AcquireUnix(string socketPath)
    {
        if (!File.Exists(socketPath))
            return new SingleInstanceGuard(null, held: true);

        switch (Probe(socketPath))
        {
            case SocketProbe.Live:
                return new SingleInstanceGuard(null, held: false); // another instance is serving.

            case SocketProbe.StaleSocket:
                // Confirmed a socket with no listener — clear it so the transport can bind fresh.
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
                // Indeterminate: a non-socket file, a peer mid-restart, or a permission error. Never
                // delete and treat as not-primary, so we cannot destroy an unrelated file or race a peer.
                return new SingleInstanceGuard(null, held: false);
        }
    }

    private static SocketProbe Probe(string socketPath)
    {
        // Step 1 — classify the file type. A Unix domain socket's inode cannot be opened as a normal
        // file: on Linux open() on a socket fails immediately with ENXIO (surfaced as IOException), and
        // never blocks. A REGULAR file opens fine. So a successful open proves it is NOT a socket, and we
        // must refuse without deleting; a failed open is the signal that it IS a socket (proceed to the
        // liveness probe). FileShare.ReadWrite avoids a sharing-violation false negative on a real file.
        if (!IsUnixSocket(socketPath, out SocketProbe classification))
            return classification;

        // Step 2 — liveness probe (only now that we know it is a socket). A successful connect means a
        // live listener; ECONNREFUSED means the socket is bound on disk but no process is listening
        // (a crashed instance) ⇒ stale and safe to clear; anything else is indeterminate.
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return SocketProbe.Live;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return SocketProbe.StaleSocket;
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
        {
            return SocketProbe.Indeterminate;
        }
    }

    // Attempts to open the path as a normal file to classify its type, AOT-safely with only the BCL (no
    // stat() P/Invoke). Returns true when the path is a Unix socket (open failed the way a socket does);
    // returns false with a non-socket <paramref name="classification"/> when it is a regular file (open
    // succeeded) or could not be classified (permission error etc.) — both of which must NOT be deleted.
    private static bool IsUnixSocket(string socketPath, out SocketProbe classification)
    {
        try
        {
            using var stream = new FileStream(socketPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // Opened as a regular file ⇒ NOT a socket ⇒ must refuse without deleting.
            classification = SocketProbe.Indeterminate;
            return false;
        }
        catch (IOException)
        {
            // open() on a socket inode fails (ENXIO) — the expected, non-blocking signal that it IS a
            // socket. Proceed to the liveness probe.
            classification = SocketProbe.Indeterminate; // unused when returning true.
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException)
        {
            // Cannot classify (e.g. permission denied) — be safe: do not delete, refuse.
            classification = SocketProbe.Indeterminate;
            return false;
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
