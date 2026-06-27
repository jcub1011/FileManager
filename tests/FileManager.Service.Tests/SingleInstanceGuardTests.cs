using System.Runtime.InteropServices;
using Xunit;

namespace FileManager.Service.Tests;

/// <summary>
/// Verifies one-service-per-user: a second guard for the same endpoint name detects the first and
/// refuses (is not the primary instance). On Linux the guard keys off a Unix socket path, so the test
/// stands up a listening socket to simulate a live first instance.
/// </summary>
public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void SecondGuard_ForSameWindowsEndpoint_Refuses()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Windows mutex path only.

        string endpoint = $"filemanager-test-{Guid.NewGuid():N}";

        using SingleInstanceGuard first = SingleInstanceGuard.Acquire(endpoint);
        Assert.True(first.IsPrimaryInstance);

        using SingleInstanceGuard second = SingleInstanceGuard.Acquire(endpoint);
        Assert.False(second.IsPrimaryInstance); // detects the first, refuses.
    }

    [Fact]
    public void FirstGuard_ReleasesOnDispose_AllowingReacquire()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        string endpoint = $"filemanager-test-{Guid.NewGuid():N}";

        using (SingleInstanceGuard first = SingleInstanceGuard.Acquire(endpoint))
            Assert.True(first.IsPrimaryInstance);

        // After the first releases, a fresh guard becomes primary again.
        using SingleInstanceGuard reacquired = SingleInstanceGuard.Acquire(endpoint);
        Assert.True(reacquired.IsPrimaryInstance);
    }

    [Fact]
    public void SecondGuard_ForLiveUnixSocket_Refuses()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Unix socket path only.

        string socketPath = Path.Combine(Path.GetTempPath(), $"fm-guard-{Guid.NewGuid():N}.sock");
        using var listener = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Unspecified);
        try
        {
            listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1); // a live "first instance" is now serving the path.

            using SingleInstanceGuard second = SingleInstanceGuard.Acquire(socketPath);
            Assert.False(second.IsPrimaryInstance);
        }
        finally
        {
            listener.Dispose();
            if (File.Exists(socketPath))
                File.Delete(socketPath);
        }
    }

    [Fact]
    public void StaleUnixSocket_NoListener_IsCleared_AndGuardBecomesPrimary()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        // A GENUINE stale socket: bind (which creates the socket file) but never Listen, then close —
        // a connect now fails with ConnectionRefused, the signal of a crashed prior instance.
        string socketPath = Path.Combine(Path.GetTempPath(), $"fm-stale-{Guid.NewGuid():N}.sock");
        var bound = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Unspecified);
        bound.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));
        bound.Dispose(); // file remains on disk, but nothing is listening.

        try
        {
            Assert.True(File.Exists(socketPath));
            using SingleInstanceGuard guard = SingleInstanceGuard.Acquire(socketPath);
            Assert.True(guard.IsPrimaryInstance);
            Assert.False(File.Exists(socketPath)); // the stale socket was cleared.
        }
        finally
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);
        }
    }

    [Fact]
    public void NonSocketFile_IsNotDeleted_GuardRefuses()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        // FIX 4 safety property: a regular (non-socket) file at the endpoint path must NEVER be deleted.
        string path = Path.Combine(Path.GetTempPath(), $"fm-notsock-{Guid.NewGuid():N}");
        File.WriteAllText(path, "important data");
        try
        {
            using SingleInstanceGuard guard = SingleInstanceGuard.Acquire(path);
            Assert.False(guard.IsPrimaryInstance);  // indeterminate ⇒ refuse, don't race/destroy.
            Assert.True(File.Exists(path));         // the unrelated file is untouched.
            Assert.Equal("important data", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
