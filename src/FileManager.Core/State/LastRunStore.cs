using System.IO;
using System.Text.Json;
using FileManager.Core.Configuration;
using FileManager.Core.Profiles;

namespace FileManager.Core.State;

/// <summary>
/// The on-disk document backing <see cref="LastRunStore"/>: a map from Profile ID to that Profile's
/// last scheduled-run timestamp (UTC). A sealed record so it serializes through the
/// <see cref="ProfileJsonContext"/> source generator (AOT-clean — no reflection).
/// </summary>
public sealed record LastRunState
{
    /// <summary>Per-Profile last-run timestamps, keyed by <c>ProfileId</c>.</summary>
    public Dictionary<string, DateTimeOffset> LastRuns { get; init; } = new();
}

/// <summary>
/// Durably persists per-Profile last-run timestamps for the scheduler's missed-run logic (§3.2.2).
/// Stored as a small JSON file under the config dir (sibling to <c>config.json</c>), (de)serialized via
/// the source generator. Following the repo's "validation, not exceptions" rule, loads never throw on a
/// bad or missing file — an absent/corrupt file simply yields "no recorded runs", so the scheduler
/// treats every Profile as never-run (and, under <c>Skip</c>/first-start, just waits for the next due
/// time).
/// </summary>
/// <remarks>
/// Writes are best-effort and serialized under a lock so concurrent scheduler threads cannot interleave
/// a partial document. Each <see cref="SetLastRun"/> rewrites the whole (small) file via a temp +
/// atomic replace, so a crash mid-write leaves the prior good file intact.
/// </remarks>
public sealed class LastRunStore
{
    /// <summary>Default file name under the config directory.</summary>
    public const string DefaultFileName = "schedule-state.json";

    private readonly string _path;
    private readonly object _gate = new();
    private LastRunState _state;

    /// <summary>The resolved state-file path.</summary>
    public string Path => _path;

    /// <summary>Creates a store backed by <paramref name="path"/>, loading any existing state.</summary>
    public LastRunStore(string path)
    {
        _path = path;
        _state = Load(path);
    }

    /// <summary>
    /// Resolves a <see cref="LastRunStore"/> at <c>&lt;config&gt;/schedule-state.json</c>.
    /// <paramref name="configDirectory"/> overrides the base config dir (tests).
    /// </summary>
    public static LastRunStore FromConfig(string? configDirectory = null)
    {
        string dir = configDirectory ?? ConfigPaths.GetConfigDirectory();
        return new LastRunStore(System.IO.Path.Combine(dir, DefaultFileName));
    }

    /// <summary>The recorded last-run timestamp for <paramref name="profileId"/>, or null if none.</summary>
    public DateTimeOffset? GetLastRun(string profileId)
    {
        lock (_gate)
            return _state.LastRuns.TryGetValue(profileId, out DateTimeOffset value) ? value : null;
    }

    /// <summary>
    /// Records <paramref name="profileId"/>'s last run as <paramref name="timestamp"/> and persists.
    /// Best-effort: a write failure leaves the in-memory value updated but is not fatal (the next
    /// successful write reconciles it).
    /// </summary>
    public void SetLastRun(string profileId, DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            _state.LastRuns[profileId] = timestamp;
            Save(_path, _state);
        }
    }

    private static LastRunState Load(string path)
    {
        if (!File.Exists(path))
            return new LastRunState();

        try
        {
            string json = File.ReadAllText(path);
            LastRunState? state = JsonSerializer.Deserialize(json, ProfileJsonContext.Default.LastRunState);
            return state ?? new LastRunState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Validation-not-exceptions: a missing/corrupt/unreadable file means "no recorded runs".
            return new LastRunState();
        }
    }

    private static void Save(string path, LastRunState state)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(state, ProfileJsonContext.Default.LastRunState);
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort persistence; the in-memory state is still authoritative for this session.
        }
    }
}
