using System.IO;
using FileManager.Core.Configuration;

namespace FileManager.Core.Jobs;

/// <summary>Host-level knobs for a <see cref="JobEngine"/>.</summary>
public sealed record JobEngineOptions
{
    /// <summary>
    /// Folder for the <c>MoveToTrash</c> placeholder. Defaults to <c>trash/</c> under the resolved
    /// config directory; overridable so tests don't write into the real config location.
    /// </summary>
    public string? TrashDirectory { get; init; }

    /// <summary>The effective trash directory (explicit override, else <c>&lt;config&gt;/trash</c>).</summary>
    public string ResolveTrashDirectory() =>
        TrashDirectory ?? Path.Combine(ConfigPaths.GetConfigDirectory(), "trash");
}
