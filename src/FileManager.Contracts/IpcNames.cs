using System.Runtime.InteropServices;

namespace FileManager.Contracts;

/// <summary>
/// Names of the local-only IPC transport endpoints used for GUI↔Service and Shell↔Service
/// communication (spec §2.1): a named pipe on Windows and a Unix domain socket on Linux/macOS.
/// No network listener is opened; endpoints are scoped per user.
/// </summary>
/// <remarks>
/// This type provides <em>names and helpers only</em>. The wire transport (length-prefixed JSON
/// messages over these endpoints) is implemented in M6.
/// </remarks>
public static class IpcNames
{
    /// <summary>Prefix of the Windows named pipe; the current user is appended.</summary>
    public const string WindowsPipePrefix = "filemanager-";

    /// <summary>File name of the Unix domain socket within <c>$XDG_RUNTIME_DIR</c>.</summary>
    public const string UnixSocketFileName = "filemanager.sock";

    /// <summary>
    /// The Windows named-pipe name for the current user, e.g. <c>filemanager-alice</c>.
    /// Pass this (without the <c>\\.\pipe\</c> prefix) to <c>NamedPipeServerStream</c>/<c>Client</c>,
    /// which prepend the prefix themselves.
    /// </summary>
    public static string GetWindowsPipeName() => GetWindowsPipeName(Environment.UserName);

    /// <summary>The Windows named-pipe name for a specific user.</summary>
    public static string GetWindowsPipeName(string userName) => WindowsPipePrefix + userName;

    /// <summary>
    /// The fully-qualified Windows pipe path, e.g. <c>\\.\pipe\filemanager-alice</c>. Useful for
    /// display/logging; the <see cref="System.IO.Pipes"/> API takes the bare name from
    /// <see cref="GetWindowsPipeName()"/>.
    /// </summary>
    public static string GetWindowsPipePath() => $@"\\.\pipe\{GetWindowsPipeName()}";

    /// <summary>
    /// The Unix domain socket path: <c>$XDG_RUNTIME_DIR/filemanager.sock</c>. When
    /// <c>XDG_RUNTIME_DIR</c> is unset, falls back to a <em>per-user</em> subdirectory under
    /// <c>/tmp</c> (<c>/tmp/filemanager-&lt;user&gt;/filemanager.sock</c>) so the socket does not
    /// sit directly in world-writable <c>/tmp</c> where another local user could squat the path.
    /// (The M6 transport is responsible for creating that directory with owner-only permissions.)
    /// </summary>
    public static string GetUnixSocketPath()
    {
        string? runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtimeDir))
            return Path.Combine(runtimeDir, UnixSocketFileName);

        string perUserDir = Path.Combine("/tmp", $"filemanager-{Environment.UserName}");
        return Path.Combine(perUserDir, UnixSocketFileName);
    }

    /// <summary>
    /// The transport endpoint for the current OS: the Windows pipe path on Windows, otherwise the
    /// Unix socket path. Provided as a convenience for diagnostics.
    /// </summary>
    public static string GetEndpointForCurrentOS() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? GetWindowsPipePath()
            : GetUnixSocketPath();
}
