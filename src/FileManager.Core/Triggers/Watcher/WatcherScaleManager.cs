namespace FileManager.Core.Triggers.Watcher;

/// <summary>
/// Watcher scale handling (§11): keeps high-churn directories from missing events.
/// <list type="bullet">
/// <item><b>Windows:</b> sizes the <c>ReadDirectoryChangesW</c> internal buffer and recovers from a
/// buffer-overflow notification by triggering a bounded rescan of the affected root.</item>
/// <item><b>Linux:</b> detects an <c>inotify</c> watch-limit error and degrades to a periodic rescan of
/// the affected root until the watcher recovers.</item>
/// </list>
/// Both failure modes converge on the same recovery — a <see cref="RescanFallback"/> over the one
/// affected Source root — so this manager centralizes the buffer-sizing recommendation and the
/// overflow→rescan decision, leaving the actual enumeration to <see cref="RescanFallback"/>.
/// </summary>
public sealed class WatcherScaleManager
{
    /// <summary>
    /// Default <see cref="System.IO.FileSystemWatcher.InternalBufferSize"/> for a Source watcher: 64 KiB,
    /// the maximum the OS reliably honors for <c>ReadDirectoryChangesW</c>. A larger buffer absorbs more
    /// bursts before overflowing; beyond 64 KiB Windows may ignore the request, so this is the practical
    /// ceiling and the value <see cref="SystemSourceFileWatcher"/> applies.
    /// </summary>
    public const int DefaultInternalBufferBytes = 64 * 1024;

    /// <summary>Number of buffer-overflow / watch-limit recoveries performed so far (diagnostic).</summary>
    public int RecoveryCount { get; private set; }

    /// <summary>
    /// Recommends the internal buffer size (bytes) for a watcher, clamped to the
    /// <see cref="DefaultInternalBufferBytes"/> ceiling. A caller may request a smaller buffer for a
    /// low-churn root; requests above the ceiling are clamped because the OS will not honor them.
    /// </summary>
    public static int RecommendBufferBytes(int requestedBytes = DefaultInternalBufferBytes) =>
        requestedBytes <= 0
            ? DefaultInternalBufferBytes
            : Math.Min(requestedBytes, DefaultInternalBufferBytes);

    /// <summary>
    /// Handles a watcher <see cref="ISourceFileWatcher.Error"/> (Windows buffer overflow / Linux watch
    /// limit): runs a bounded rescan of <paramref name="root"/> and returns the file paths found, so the
    /// caller can re-offer the ones missed during the gap. Increments <see cref="RecoveryCount"/>.
    /// </summary>
    public IReadOnlyList<string> RecoverByRescan(string root, RescanFallback rescan)
    {
        RecoveryCount++;
        return rescan.Rescan(root);
    }
}
