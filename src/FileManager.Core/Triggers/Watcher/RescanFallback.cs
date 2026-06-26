using FileManager.Core.IO;

namespace FileManager.Core.Triggers.Watcher;

/// <summary>
/// The bounded rescan that recovers events lost to a watcher buffer-overflow / inotify watch-limit
/// (§11). When the OS notification stream drops events, the only way to catch the files that appeared
/// during the gap is to re-enumerate the affected Source root and re-offer every file to the readiness
/// pipeline. Rescan scope is deliberately bounded to <em>one</em> Source root (not the whole machine)
/// to keep recovery affordable on large trees (milestone Risk).
/// </summary>
/// <remarks>
/// The rescan does not itself decide readiness — it simply surfaces the current file set under the
/// root via the injected <see cref="IFileOperations"/>; <see cref="SourceWatcher"/> feeds each path
/// back through the same settle + <see cref="ReadinessProbe"/> path as a live event, so a file caught
/// by a rescan is held until it is stable exactly like one caught by a notification.
/// </remarks>
public sealed class RescanFallback
{
    private readonly IFileOperations _files;

    /// <summary>Creates a rescan over <paramref name="files"/>.</summary>
    public RescanFallback(IFileOperations files) => _files = files;

    /// <summary>
    /// Enumerates every file currently under <paramref name="root"/> (recursively), skipping the
    /// engine's own in-flight temp artifacts so a half-written copy is never mistaken for a Source
    /// file. Returns absolute, normalized paths.
    /// </summary>
    public IReadOnlyList<string> Rescan(string root)
    {
        var paths = new List<string>();
        foreach (string path in _files.EnumerateFiles(root, recursive: true))
        {
            if (path.EndsWith(AtomicFileWriter.TempSuffix, StringComparison.Ordinal))
                continue;
            paths.Add(PathNormalizer.Normalize(path));
        }

        return paths;
    }
}
