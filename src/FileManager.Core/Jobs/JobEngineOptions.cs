using System.IO;
using FileManager.Core.Configuration;
using FileManager.Core.Safety;

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

    /// <summary>
    /// Root under which per-Job transformer workspaces (<c>.pipeline_tmp/&lt;JobId&gt;</c>) are created.
    /// Defaults to <c>tmp/</c> under the resolved config directory; overridable so tests don't write
    /// into the real config location. May migrate to ServiceConfig/Profile in a later milestone.
    /// </summary>
    public string? PipelineTempRoot { get; init; }

    /// <summary>The effective pipeline temp root (explicit override, else <c>&lt;config&gt;/tmp</c>).</summary>
    public string ResolvePipelineTempRoot() =>
        PipelineTempRoot ?? Path.Combine(ConfigPaths.GetConfigDirectory(), "tmp");

    /// <summary>
    /// Root under which per-Job staging areas (<c>.staging/&lt;JobId&gt;</c>) hold prior Target versions
    /// moved aside under <c>StageOverwrites</c> (§6.2) until a Job succeeds (discarded) or fails
    /// (restored). Defaults to <c>staging/</c> under the resolved config directory; overridable so
    /// tests don't write into the real config location.
    /// </summary>
    public string? StagingRoot { get; init; }

    /// <summary>The effective staging root (explicit override, else <c>&lt;config&gt;/staging</c>).</summary>
    public string ResolveStagingRoot() =>
        StagingRoot ?? Path.Combine(ConfigPaths.GetConfigDirectory(), "staging");

    /// <summary>
    /// Headroom (in bytes) the proactive disk-space checks keep free on every volume; mirrors
    /// <see cref="ServiceConfig.MinFreeSpaceMarginBytes"/>. Threaded into the
    /// <see cref="SpaceReservationLedger"/> and trash free-space checks the convenience constructor
    /// builds. Defaults to <c>0</c> (refuse only when a volume genuinely cannot fit the bytes).
    /// </summary>
    public long MinFreeSpaceMarginBytes { get; init; }
}
