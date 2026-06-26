using System.IO;

namespace FileManager.Core.Triggers.Watcher;

/// <summary>
/// The production <see cref="ISourceFileWatcher"/> over a real <see cref="FileSystemWatcher"/>. Watches
/// a Source root recursively for created/changed/renamed files, sizing the internal notification buffer
/// (<see cref="WatcherScaleManager.RecommendBufferBytes"/>) to absorb bursts, and surfaces the
/// buffer-overflow / watch-limit <see cref="FileSystemWatcher.Error"/> through <see cref="Error"/> so
/// <see cref="SourceWatcher"/> can recover by rescan (§11).
/// </summary>
public sealed class SystemSourceFileWatcher : ISourceFileWatcher
{
    private readonly FileSystemWatcher _fsw;

    /// <summary>The watched Source root.</summary>
    public string Root { get; }

    /// <inheritdoc/>
    public event Action<WatcherChange>? Changed;

    /// <inheritdoc/>
    public event Action<Exception>? Error;

    /// <summary>
    /// Creates a watcher over <paramref name="root"/>. <paramref name="internalBufferBytes"/> sizes the
    /// OS notification buffer (clamped to the practical ceiling); a larger buffer overflows less often
    /// under churn.
    /// </summary>
    public SystemSourceFileWatcher(
        string root, int internalBufferBytes = WatcherScaleManager.DefaultInternalBufferBytes)
    {
        Root = IO.PathNormalizer.Normalize(root);
        _fsw = new FileSystemWatcher(Root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = WatcherScaleManager.RecommendBufferBytes(internalBufferBytes),
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        _fsw.Created += (_, e) => Changed?.Invoke(new WatcherChange(WatcherChangeKind.Created, e.FullPath));
        _fsw.Changed += (_, e) => Changed?.Invoke(new WatcherChange(WatcherChangeKind.Changed, e.FullPath));
        _fsw.Renamed += (_, e) => Changed?.Invoke(new WatcherChange(WatcherChangeKind.Renamed, e.FullPath));
        _fsw.Error += (_, e) => Error?.Invoke(e.GetException());
    }

    /// <summary>Begins raising events.</summary>
    public void Start() => _fsw.EnableRaisingEvents = true;

    public void Dispose() => _fsw.Dispose();
}
