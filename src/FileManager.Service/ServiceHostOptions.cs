namespace FileManager.Service;

/// <summary>
/// Host-level knobs for a <see cref="ServiceHost"/>, all with test-friendly defaults so production code
/// constructs it with no arguments while tests inject a fake clock, an isolated config directory, a
/// unique IPC endpoint, and manual tick control.
/// </summary>
public sealed record ServiceHostOptions
{
    /// <summary>
    /// Overrides the base config directory (where Profiles, config.json, journal, audit, logs live).
    /// Null uses the per-OS <c>ConfigPaths.GetConfigDirectory()</c>. Tests point this at a temp dir.
    /// </summary>
    public string? ConfigDirectory { get; init; }

    /// <summary>
    /// Overrides the IPC endpoint name (Windows pipe name / Linux socket path). Null uses the per-user
    /// endpoint. Tests pass a unique value so concurrent test hosts never collide.
    /// </summary>
    public string? IpcEndpointName { get; init; }

    /// <summary>The clock for the tick loop and Job timestamps. Null uses <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>
    /// The tick interval driving watcher settle pumps and scheduled-run firing. Ignored when
    /// <see cref="ManualTicks"/> is true (the test drives <see cref="ServiceHost.TickAsync"/> directly).
    /// </summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// When true the host does NOT start its own internal timer; the caller drives ticks via
    /// <see cref="ServiceHost.TickAsync"/>. Deterministic for tests; production leaves this false.
    /// </summary>
    public bool ManualTicks { get; init; }

    /// <summary>
    /// When true the host does NOT start real filesystem watchers (no <c>FileSystemWatcher</c>). Tests
    /// that exercise IPC/queue behaviour set this so no OS watch handles are opened. Production false.
    /// </summary>
    public bool DisableWatchers { get; init; }

    /// <summary>
    /// When true the host does NOT start the IPC server. Used by tests that only exercise the engine
    /// wiring (e.g. drain-on-stop) without binding an endpoint. Production false.
    /// </summary>
    public bool DisableIpc { get; init; }
}
