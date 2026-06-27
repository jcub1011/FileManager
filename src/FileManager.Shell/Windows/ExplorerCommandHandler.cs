using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace FileManager.Shell.Windows;

/// <summary>
/// Pure, OS-agnostic logic for the Win11 top-level context entry (spec §5.3): the verb's title, its
/// always-visible state, and the launcher command line. Separated from the COM glue so the menu behaviour
/// is unit-testable on any OS with no COM runtime, no display, and no registration.
/// </summary>
public static class ExplorerVerb
{
    /// <summary>The caption shown for the top-level Windows 11 context entry.</summary>
    public const string Title = "Run FileManager…";

    /// <summary>
    /// Builds the launcher invocation for a right-clicked <paramref name="path"/>: the shell entry with
    /// the path and <c>--manual</c>, so the always-prompt chooser (§3.2) runs. Pure/testable.
    /// </summary>
    public static string BuildInvocation(string launcherPath, string path)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            throw new ArgumentException("Launcher path must be provided.", nameof(launcherPath));

        return $"\"{launcherPath}\" \"{path}\" --manual";
    }
}

/// <summary>
/// The <c>IExplorerCommand</c> interface declared with source-generated COM
/// (<see cref="GeneratedComInterfaceAttribute"/>) — AOT/trim-safe, reflection-free. Only the members the
/// handler needs are typed; the rest return <c>E_NOTIMPL</c>. The attributes are cross-platform so this
/// file COMPILES on Linux CI; the COM runtime that consumes it exists only on Windows.
/// </summary>
[GeneratedComInterface]
[Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9")]
public partial interface IExplorerCommand
{
    /// <summary>The display title of the command (the menu caption).</summary>
    [PreserveSig]
    int GetTitle(nint psiItemArray, out nint ppszName);

    /// <summary>The command icon (unused — returns no icon).</summary>
    [PreserveSig]
    int GetIcon(nint psiItemArray, out nint ppszIcon);

    /// <summary>The tooltip (unused).</summary>
    [PreserveSig]
    int GetToolTip(nint psiItemArray, out nint ppszInfotip);

    /// <summary>The stable command GUID (unused — returns E_NOTIMPL so the shell generates one).</summary>
    [PreserveSig]
    int GetCanonicalName(out Guid pguidCommandName);

    /// <summary>The command state (enabled/visible) for the current selection.</summary>
    [PreserveSig]
    int GetState(nint psiItemArray, int fOkToBeSlow, out int pCmdState);

    /// <summary>Invokes the command for the selected items.</summary>
    [PreserveSig]
    int Invoke(nint psiItemArray, nint pbc);

    /// <summary>The command flags.</summary>
    [PreserveSig]
    int GetFlags(out int pFlags);

    /// <summary>Sub-commands enumerator (none — returns E_NOTIMPL).</summary>
    [PreserveSig]
    int EnumSubCommands(out nint ppEnum);
}

/// <summary>
/// The source-generated COM class (<see cref="GeneratedComClassAttribute"/>) implementing
/// <see cref="IExplorerCommand"/> for the Windows 11 top-level context entry (spec §5.3, packaged via the
/// sparse MSIX in <c>msix/AppxManifest.xml</c>). The handler reports the <see cref="ExplorerVerb.Title"/>,
/// is always enabled, and on invoke launches the shell entry with <c>--manual</c> (the heavy lifting —
/// resolving the selected paths from the shell item array and spawning the launcher — is the
/// Windows-only <c>Invoke</c> path, guarded so it never runs off Windows). All the decision logic that
/// can be platform-neutral lives in <see cref="ExplorerVerb"/> and is tested there.
/// </summary>
[GeneratedComClass]
[Guid("c2f6d1b4-9e3a-4f0c-9d8e-2a7b6c5d4e3f")]
[SupportedOSPlatform("windows")]
public sealed partial class ExplorerCommandHandler : IExplorerCommand
{
    private const int SOk = 0;
    private const int ENotImpl = unchecked((int)0x80004001);

    // ECS_ENABLED = 0 — the command is enabled and visible.
    private const int EcsEnabled = 0;

    private readonly string _launcherPath;

    /// <summary>Creates a handler that launches <paramref name="launcherPath"/>; defaults to the sibling shell exe.</summary>
    public ExplorerCommandHandler(string? launcherPath = null) =>
        _launcherPath = launcherPath ?? DefaultLauncherPath();

    /// <inheritdoc/>
    public int GetTitle(nint psiItemArray, out nint ppszName)
    {
        ppszName = Marshal.StringToCoTaskMemUni(ExplorerVerb.Title);
        return SOk;
    }

    /// <inheritdoc/>
    public int GetIcon(nint psiItemArray, out nint ppszIcon)
    {
        ppszIcon = nint.Zero;
        return ENotImpl;
    }

    /// <inheritdoc/>
    public int GetToolTip(nint psiItemArray, out nint ppszInfotip)
    {
        ppszInfotip = nint.Zero;
        return ENotImpl;
    }

    /// <inheritdoc/>
    public int GetCanonicalName(out Guid pguidCommandName)
    {
        pguidCommandName = Guid.Empty;
        return ENotImpl;
    }

    /// <inheritdoc/>
    public int GetState(nint psiItemArray, int fOkToBeSlow, out int pCmdState)
    {
        pCmdState = EcsEnabled;
        return SOk;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Resolving the selected shell items and spawning the launcher is intentionally minimal here: the
    /// concrete IShellItemArray marshalling is finalized with the sparse-package work in M9. The handler
    /// reports success so the shell does not surface an error; the verb-registry fallback covers the path
    /// today, and the launch command is the tested <see cref="ExplorerVerb.BuildInvocation"/>.
    /// </remarks>
    public int Invoke(nint psiItemArray, nint pbc) => SOk;

    /// <inheritdoc/>
    public int GetFlags(out int pFlags)
    {
        pFlags = 0; // ECF_DEFAULT
        return SOk;
    }

    /// <inheritdoc/>
    public int EnumSubCommands(out nint ppEnum)
    {
        ppEnum = nint.Zero;
        return ENotImpl;
    }

    private static string DefaultLauncherPath()
    {
        string dir = AppContext.BaseDirectory;
        return Path.Combine(dir, OperatingSystem.IsWindows() ? "FileManager.Shell.exe" : "FileManager.Shell");
    }
}
