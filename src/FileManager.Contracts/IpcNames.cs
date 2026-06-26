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
    /// The Unix domain socket path: <c>$XDG_RUNTIME_DIR/filemanager.sock</c>, falling back to
    /// <c>/tmp</c> when <c>XDG_RUNTIME_DIR</c> is unset.
    /// </summary>
    public static string GetUnixSocketPath()
    {
        string? runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string baseDir = !string.IsNullOrEmpty(runtimeDir) ? runtimeDir : "/tmp";
        return Path.Combine(baseDir, UnixSocketFileName);
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
