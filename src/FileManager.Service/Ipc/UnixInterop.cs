using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FileManager.Service.Ipc;

/// <summary>
/// Minimal libc interop for hardening the Unix domain socket file at creation. The single import is a
/// fully blittable signature (<see cref="uint"/> in/out) so a classic <see cref="DllImportAttribute"/>
/// is AOT-safe with no reflection and no unsafe code; it is guarded by
/// <see cref="SupportedOSPlatformAttribute"/> so it only compiles/runs on non-Windows.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static class UnixInterop
{
    /// <summary>
    /// Sets the process file-mode creation mask and returns the previous mask. Setting it to <c>0077</c>
    /// around <c>bind()</c> forces a freshly created socket file to be owner-only, closing the TOCTOU
    /// window between bind and an explicit chmod.
    /// </summary>
    [DllImport("libc", EntryPoint = "umask")]
    internal static extern uint Umask(uint mask);

    /// <summary>The umask that yields owner-only (0600) files: deny group + other (octal 0077 = 0x3F).</summary>
    internal const uint OwnerOnlyMask = 0x3F;

    /// <summary>
    /// Runs <paramref name="action"/> with the process umask set to <see cref="OwnerOnlyMask"/>, then
    /// restores the previous mask. Best-effort: if the platform does not honor umask the action still
    /// runs (the explicit chmod and the 0700 parent dir remain as defense-in-depth).
    /// </summary>
    internal static void WithOwnerOnlyUmask(Action action)
    {
        uint previous = Umask(OwnerOnlyMask);
        try
        {
            action();
        }
        finally
        {
            Umask(previous);
        }
    }
}
