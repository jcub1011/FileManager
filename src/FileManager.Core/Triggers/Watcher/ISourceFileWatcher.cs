namespace FileManager.Core.Triggers.Watcher;

/// <summary>The kind of filesystem change a watcher reports for a path.</summary>
public enum WatcherChangeKind
{
    /// <summary>A file was created.</summary>
    Created,

    /// <summary>A file's contents changed (it may still be growing).</summary>
    Changed,

    /// <summary>A file was renamed into the watched tree (treated like a create of the new name).</summary>
    Renamed,
}

/// <summary>One change event surfaced by a watcher.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="FullPath">The absolute path of the affected file.</param>
public sealed record WatcherChange(WatcherChangeKind Kind, string FullPath);

/// <summary>
/// A seam over <see cref="System.IO.FileSystemWatcher"/> so <see cref="SourceWatcher"/> can be driven
/// by synthetic create/change/overflow events in tests (no real filesystem events, which are inherently
/// non-deterministic). The production implementation wraps a real <c>FileSystemWatcher</c>; a test
/// double simply raises the events on demand.
/// </summary>
/// <remarks>
/// <para><see cref="Changed"/> fires for each create/change/rename within the watched root.
/// <see cref="Error"/> fires when the underlying OS notification buffer overflows (Windows
/// <c>ReadDirectoryChangesW</c> <c>InternalBufferOverflowException</c>) or the watch limit is hit
/// (Linux inotify), signalling that events were dropped and a rescan is required (§11).</para>
/// </remarks>
public interface ISourceFileWatcher : IDisposable
{
    /// <summary>The Source root being watched.</summary>
    public string Root { get; }

    /// <summary>Raised for each create/change/rename event under <see cref="Root"/>.</summary>
    public event Action<WatcherChange>? Changed;

    /// <summary>Raised when the OS notification buffer overflowed / the watch limit was hit (events lost).</summary>
    public event Action<Exception>? Error;

    /// <summary>Begins delivering events.</summary>
    public void Start();
}
