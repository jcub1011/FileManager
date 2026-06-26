using System.IO;
using FileManager.Core.IO;

namespace FileManager.Core.Tests;

/// <summary>
/// A deterministic <see cref="IFreeSpaceProbe"/> for tests. A path resolves to a volume key by
/// longest-prefix match over a configured volume→free-bytes map; an unmatched path is treated as the
/// (default) unconstrained volume so probes never false-fail unless a test opts a volume in.
/// </summary>
internal sealed class FakeFreeSpaceProbe : IFreeSpaceProbe
{
    private readonly Dictionary<string, long> _volumes;
    private readonly long _defaultAvailable;

    /// <summary>
    /// Creates a probe. <paramref name="volumes"/> maps a volume-root path (or any directory prefix
    /// the test wants to treat as a distinct constrained volume) to its free bytes; a path under no
    /// configured volume reports <paramref name="defaultAvailable"/> (default: unconstrained).
    /// </summary>
    public FakeFreeSpaceProbe(
        IReadOnlyDictionary<string, long>? volumes = null,
        long defaultAvailable = long.MaxValue)
    {
        _volumes = new Dictionary<string, long>();
        if (volumes is not null)
        {
            foreach ((string key, long value) in volumes)
                _volumes[PathNormalizer.Normalize(key)] = value;
        }

        _defaultAvailable = defaultAvailable;
    }

    /// <summary>An unconstrained probe — every volume reports <see cref="long.MaxValue"/>.</summary>
    public static FakeFreeSpaceProbe Unconstrained() => new();

    public VolumeSpace Probe(string path)
    {
        string full = PathNormalizer.Normalize(path);

        string? bestKey = null;
        int bestLen = -1;
        foreach ((string key, long _) in _volumes)
        {
            if (!IsUnderKey(full, key))
                continue;
            if (key.Length > bestLen)
            {
                bestKey = key;
                bestLen = key.Length;
            }
        }

        if (bestKey is not null)
            return new VolumeSpace(bestKey, _volumes[bestKey]);

        // No configured volume owns the path. Use the OS path root as the key (so two unconfigured
        // paths on the same drive still aggregate together) and the default free figure.
        string root = Path.GetPathRoot(full) is { Length: > 0 } r ? PathNormalizer.Normalize(r) : full;
        return new VolumeSpace(root, _defaultAvailable);
    }

    private static bool IsUnderKey(string full, string key)
    {
        if (full.Equals(key, PathNormalizer.Comparison))
            return true;
        string keyWithSep = key.EndsWith(Path.DirectorySeparatorChar) ? key : key + Path.DirectorySeparatorChar;
        return full.StartsWith(keyWithSep, PathNormalizer.Comparison);
    }
}
