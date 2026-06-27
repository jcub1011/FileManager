using System.Runtime.InteropServices;

namespace FileManager.Gui;

/// <summary>
/// Enforces one running GUI per user via a cross-platform named <see cref="Mutex"/>. The shell launcher's
/// manual-invocation flow (M8 §3.2) relies on a SINGLE GUI being up so exactly one chooser subscriber
/// exists: a second right-click that cold-starts the GUI must detect the already-running instance and exit
/// cleanly, leaving the first (subscribed) instance to receive the pending (delivered live or via the
/// service's replay-on-subscribe). Unlike the Service guard, the GUI opens no IPC endpoint, so a named
/// mutex — not a socket probe — is the right primitive; .NET named mutexes work on Windows and Linux.
/// </summary>
/// <remarks>
/// The decisive "another instance exists" signal is <c>createdNew == false</c> from the named-object
/// constructor (avoiding the per-thread reentrancy of <see cref="Mutex.WaitOne(TimeSpan, bool)"/>). The
/// name is injectable so tests can claim/contend a unique slot deterministically without touching the
/// real per-user name.
/// </remarks>
public sealed class GuiSingleInstanceGuard : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly bool _held;

    private GuiSingleInstanceGuard(Mutex? mutex, bool held)
    {
        _mutex = mutex;
        _held = held;
    }

    /// <summary>True when this process holds the single-instance claim (no other GUI is running).</summary>
    public bool IsPrimaryInstance => _held;

    /// <summary>
    /// Attempts to claim the single-GUI slot for the current user. <paramref name="name"/> overrides the
    /// per-user mutex name (tests pass a unique name); null derives it from the current user.
    /// </summary>
    public static GuiSingleInstanceGuard Acquire(string? name = null)
    {
        string mutexName = name ?? DefaultName();

        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName, out createdNew);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Cannot create the named object (rare) — fail open so the GUI still runs rather than refusing.
            return new GuiSingleInstanceGuard(null, held: true);
        }

        if (!createdNew)
        {
            // Another instance already owns the named object — we are secondary.
            mutex.Dispose();
            return new GuiSingleInstanceGuard(null, held: false);
        }

        bool held;
        try
        {
            held = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // A previous owner exited without releasing; we now own it.
            held = true;
        }

        return new GuiSingleInstanceGuard(mutex, held);
    }

    // A per-user, cross-platform mutex name. "Local\" scopes it to the session on Windows; on Linux the
    // prefix is ignored. Environment.UserName keys it per-user so two users each get their own GUI.
    private static string DefaultName()
    {
        string slug = new string(Environment.UserName
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (slug.Length == 0)
            slug = "user";
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $@"Local\FileManager.Gui-{slug}"
            : $"FileManager.Gui-{slug}";
    }

    /// <summary>Releases the single-instance claim.</summary>
    public void Dispose()
    {
        if (_mutex is null)
            return;

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
        }

        _mutex.Dispose();
    }
}
