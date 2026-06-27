using System.IO;
using FileManager.Contracts.Messages;
using FileManager.Core.Audit;
using FileManager.Core.Configuration;
using FileManager.Core.Disposition;
using FileManager.Core.Execution;
using FileManager.Core.Filtering;
using FileManager.Core.IO;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Logging;
using FileManager.Core.Metadata;
using FileManager.Core.Profiles;
using FileManager.Core.Recovery;
using FileManager.Core.Routing;
using FileManager.Core.Safety;
using FileManager.Core.State;
using FileManager.Core.Transformers;
using FileManager.Core.Trash;
using FileManager.Core.Triggers.Schedule;
using FileManager.Core.Triggers.Watcher;
using FileManager.Service.Ipc;
using FileManager.Service.Tray;

namespace FileManager.Service;

/// <summary>
/// The minimal, dependency-free Core Service host (spec §1.1 / §2). On <see cref="StartAsync"/> it loads
/// the <see cref="ServiceConfig"/>, builds the durable log/journal/audit, runs M4 crash recovery, loads
/// Profiles, builds the shared <see cref="PathLockManager"/> + <see cref="SpaceReservationLedger"/> + one
/// <see cref="JobEngine"/>, stands up the <see cref="JobQueue"/> + <see cref="WorkerPool"/> (handler →
/// <see cref="JobEngine.ProcessFile"/>), runs the scheduler startup sweep, attaches a
/// <see cref="SourceWatcher"/> per Source root, and starts the IPC server. A tick loop (injected
/// <see cref="TimeProvider"/>) pumps each watcher and fires due scheduled runs. <see cref="StopAsync"/>
/// stops accepting, drains the pool, stops the watchers/timer, and flushes/disposes the durable files.
/// </summary>
/// <remarks>
/// This is a hand-rolled host rather than <c>Microsoft.Extensions.Hosting</c> so the executable takes no
/// NuGet/DI dependency and stays AOT-clean (matching the codebase's ethos). It implements
/// <see cref="IEngineFacade"/> directly: the IPC layer talks to the host through that narrow surface.
/// </remarks>
public sealed class ServiceHost : IEngineFacade, IAsyncDisposable
{
    private readonly ServiceHostOptions _options;
    private readonly TimeProvider _clock;
    private readonly IFileOperations _files;
    private readonly Lock _stateGate = new();

    // Durable + engine collaborators, built in StartAsync and owned for the host's lifetime.
    private RotatingLogWriter? _log;
    private FileJournal? _journal;
    private AuditLog? _audit;
    private JobEngine? _engine;
    private JobQueue? _queue;
    private WorkerPool? _pool;
    private Scheduler? _scheduler;
    private LastRunStore? _lastRuns;
    private PayloadQueue? _payloads;
    private EventBroadcaster? _events;
    private IpcServer? _ipc;
    private ITrayIndicator _tray = NullTrayIndicator.Instance;
    private string _configDir = string.Empty;
    private string _trashDirectory = string.Empty;

    private readonly List<SourceWatcher> _watchers = new();
    private volatile IReadOnlyList<Profile> _profiles = Array.Empty<Profile>();

    // Live counters for the EngineState snapshot. Enqueued - Started == queued depth; in-flight is the
    // running delta; terminal tallies accumulate. Interlocked so the IPC thread reads a consistent value.
    private long _enqueued;
    private long _started;
    private long _inFlight;
    private long _closed;
    private long _skipped;
    private long _failed;

    private Timer? _tickTimer;
    private volatile bool _started_flag;

    /// <summary>The IPC endpoint the host is serving on (diagnostics); empty before <see cref="StartAsync"/>.</summary>
    public string Endpoint => _ipc?.Endpoint ?? string.Empty;

    /// <summary>The number of active <see cref="SourceWatcher"/>s (diagnostic/tests).</summary>
    public int WatcherCount => _watchers.Count;

    /// <summary>Creates a host with the given options (all defaulted for production).</summary>
    public ServiceHost(ServiceHostOptions? options = null)
    {
        _options = options ?? new ServiceHostOptions();
        _clock = _options.Clock ?? TimeProvider.System;
        _files = new SystemFileOperations();
    }

    /// <summary>
    /// Builds and starts the whole engine + triggers + IPC. Throws only on an unrecoverable startup
    /// fault (e.g. cannot create the durable files); config problems are logged and the host runs with
    /// the validated defaults (validation-not-exceptions).
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // 1. Config (validation, not exceptions): a bad/absent file yields validated defaults.
        ServiceConfigLoadResult configResult = LoadConfig();
        ServiceConfig config = configResult.Config;
        _configDir = _options.ConfigDirectory ?? ConfigPaths.GetConfigDirectory();

        // 2. Durable log / journal / audit from config (resolved under the host's config dir).
        _log = RotatingLogWriter.FromConfig(config, _configDir);
        _journal = FileJournal.FromConfig(config, _configDir);
        _audit = AuditLog.FromConfig(config, _configDir);

        if (!configResult.IsValid)
            LogLine(LogSeverity.Failure, "CONFIG_INVALID",
                $"config.json invalid; using defaults: {string.Join("; ", configResult.Validation.Errors.Select(e => e.Message))}");

        var engineOptions = new JobEngineOptions
        {
            TrashDirectory = Path.Combine(_configDir, "trash"),
            PipelineTempRoot = Path.Combine(_configDir, "tmp"),
            StagingRoot = Path.Combine(_configDir, "staging"),
            MinFreeSpaceMarginBytes = config.MinFreeSpaceMarginBytes,
        };
        // Remembered for the dry-run preview's MoveToTrash destination (the engine reports the local
        // trash-fallback folder; native trash applies at execution time).
        _trashDirectory = engineOptions.ResolveTrashDirectory();

        // 3. M4 crash recovery: bring any OPEN Jobs to a safe terminal state before accepting new work.
        var recovery = new RecoveryService(_journal, new RollbackEngine(_files), _files, engineOptions);
        RecoveryReport report = recovery.Recover();
        if (report.Jobs.Count > 0)
            LogLine(LogSeverity.Info, "RECOVERY", $"Recovered {report.Jobs.Count} open Job(s) on startup.");

        // 4. Profiles.
        ReloadProfilesInternal();

        // 5. Shared engine collaborators (ONE PathLockManager + ONE ledger across the pool).
        var pathLocks = new PathLockManager();
        var ledger = new SpaceReservationLedger(new SystemFreeSpaceProbe(), config.MinFreeSpaceMarginBytes);
        int workers = config.MaxWorkers > 0 ? config.MaxWorkers : 1;
        _engine = BuildEngine(engineOptions, ledger, pathLocks, workers);

        // 6. Queue + pool (handler runs the §4 lifecycle, with in-flight/terminal accounting + events).
        _queue = new JobQueue();
        _pool = new WorkerPool(_queue, workers, RunJob, pathLocks, OnJobError);

        _events = new EventBroadcaster();
        _payloads = new PayloadQueue(
            _queue, () => _profiles, _files, _clock,
            onEnqueued: count => Interlocked.Add(ref _enqueued, count));

        // 7. Scheduler startup sweep: fire any catch-up runs immediately, enqueuing their files.
        _lastRuns = LastRunStore.FromConfig(_configDir);
        _scheduler = new Scheduler(_lastRuns, _log, _clock);
        _scheduler.RunStartupSweep(_profiles, EnqueueScheduledRun);

        // 8. Watchers (one per Source root) unless disabled for a test.
        if (!_options.DisableWatchers)
            BuildWatchers();

        // 9. Tray (attach only when available; absence is a no-op).
        if (TrayAvailability.IsAvailable())
        {
            _tray = NullTrayIndicator.Instance; // M6: abstraction only; real rendering is M7.
            _tray.Show();
            _tray.SetStatus("FileManager running");
        }

        // 10. IPC server.
        if (!_options.DisableIpc)
        {
            IIpcServerTransport transport = IpcServer.CreateTransportForCurrentOS(_options.IpcEndpointName);
            var dispatcher = new ConnectionDispatcher(this, _events, _log);
            _ipc = new IpcServer(transport, dispatcher, _log);
            _ipc.Start();
        }

        // 11. Tick loop (production) unless the test drives ticks manually.
        if (!_options.ManualTicks)
        {
            _tickTimer = new Timer(_ => SafeTick(), null, _options.TickInterval, _options.TickInterval);
        }

        _started_flag = true;
        LogLine(LogSeverity.Info, "STARTED", $"Service started on endpoint '{Endpoint}'.");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private ServiceConfigLoadResult LoadConfig() =>
        _options.ConfigDirectory is null
            ? ServiceConfigStore.Load()
            : ServiceConfigStore.LoadFrom(Path.Combine(_options.ConfigDirectory, ConfigPaths.ConfigFileName));

    private JobEngine BuildEngine(
        JobEngineOptions options, SpaceReservationLedger ledger, PathLockManager pathLocks, int workers) =>
        new(
            _files,
            _log!,
            new FilterEvaluator(new DedupeIndex(_files)),
            new TransformerRunner(_files, new SystemProcessRunner()),
            new ConflictResolver(_files),
            new SourceDisposer(_files, TrashServiceFactory.Create(
                _files, options.ResolveTrashDirectory(), freeSpace: null, options.MinFreeSpaceMarginBytes)),
            verifier: null,
            new MetadataCopier(_files),
            new RollbackEngine(_files),
            new SystemFreeSpaceProbe(),
            ledger,
            options,
            _journal,
            _audit,
            pathLocks,
            targetParallelism: workers);

    // The worker-pool handler: runs the §4 lifecycle, maintains in-flight/terminal counters, and pushes
    // a JobEvent for the terminal state to subscribers. Never throws (the engine returns a Failed result
    // for ordinary I/O); a thrown exception is routed to OnJobError by the pool.
    private JobResult RunJob(JobRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _started);
        Interlocked.Increment(ref _inFlight);
        try
        {
            JobResult result = _engine!.ProcessFile(request.Profile, request.SourcePath, request.Context);
            RecordTerminal(result.State);
            PublishEvent(request, result);
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void OnJobError(JobRequest request, Exception ex)
    {
        Interlocked.Increment(ref _failed);
        LogLine(LogSeverity.Failure, "JOB_ERROR", $"{request.SourcePath}: {ex.Message}");
        PublishEvent(request, new JobResult
        {
            JobId = string.Empty,
            State = JobState.Failed,
            SourcePath = request.SourcePath,
            FailureReason = ex.Message,
        });
    }

    private void RecordTerminal(JobState state)
    {
        switch (state)
        {
            case JobState.Closed:
                Interlocked.Increment(ref _closed);
                break;
            case JobState.Skipped:
                Interlocked.Increment(ref _skipped);
                break;
            case JobState.Failed:
                Interlocked.Increment(ref _failed);
                break;
        }
    }

    private void PublishEvent(JobRequest request, JobResult result)
    {
        _events?.Publish(new JobEvent(
            JobId: $"{request.Profile.ProfileId}:{request.SourcePath}",
            ProfileId: request.Profile.ProfileId,
            State: result.State.ToString(),
            Code: result.State switch
            {
                JobState.Closed => "COMPLETED",
                JobState.Skipped => "SKIPPED",
                JobState.Failed => "FAILED",
                _ => "PROGRESS",
            },
            Message: result.FailureReason ?? result.State.ToString(),
            Timestamp: _clock.GetUtcNow()));
    }

    private void BuildWatchers()
    {
        var roots = new Dictionary<string, (int Settle, int Stability)>(StringComparer.OrdinalIgnoreCase);
        foreach (Profile profile in _profiles)
        {
            if (!profile.Triggers.Watcher)
                continue;
            foreach (SourceSpec source in profile.Sources)
            {
                string root = PathNormalizer.Normalize(source.Path);
                if (!roots.ContainsKey(root))
                    roots[root] = (source.SettleDelaySeconds, source.StabilityIntervalMs);
            }
        }

        var rescan = new RescanFallback(_files);
        foreach ((string root, (int settle, int stability)) in roots)
        {
            try
            {
                var watcher = new SourceWatcher(
                    new SystemSourceFileWatcher(root),
                    new ReadinessProbe(),
                    new WatcherScaleManager(),
                    rescan,
                    _log!,
                    settle,
                    stability,
                    path => EnqueueWatchedFile(root, path),
                    _clock);
                watcher.Start();
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                LogLine(LogSeverity.Failure, "WATCHER_START", $"Could not watch '{root}': {ex.Message}");
            }
        }
    }

    // A watcher reported a ready file: submit it (the resolver picks the owning Profile by Source).
    private void EnqueueWatchedFile(string root, string path) =>
        _payloads?.Submit(new SubmitPayload(path, ProfileId: null, Recursive: false));

    // A scheduled Profile is due: submit every Source root recursively under that Profile's id.
    private void EnqueueScheduledRun(Profile profile, DateTimeOffset firedAt)
    {
        foreach (SourceSpec source in profile.Sources)
            _payloads?.Submit(new SubmitPayload(source.Path, profile.ProfileId, Recursive: true));
    }

    /// <summary>
    /// Runs one tick: pumps every watcher's settle window and fires any scheduled runs that became due.
    /// Public so a test (with <see cref="ServiceHostOptions.ManualTicks"/>) can advance the clock and
    /// drive the loop deterministically.
    /// </summary>
    public Task TickAsync()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        foreach (SourceWatcher watcher in _watchers)
            watcher.Tick(now);

        if (_scheduler is not null)
        {
            foreach (Profile profile in _profiles)
            {
                MissedRunDecision? decision = _scheduler.EvaluateStart(profile);
                if (decision is { NextDue: { } due } && due <= now)
                    _scheduler.FireDue(profile, now, EnqueueScheduledRun);
            }
        }

        return Task.CompletedTask;
    }

    private void SafeTick()
    {
        try
        {
            _ = TickAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            LogLine(LogSeverity.Failure, "TICK_FAULT", $"Tick failed: {ex.Message}");
        }
    }

    /// <summary>The grace period a graceful drain is given before falling back to a hard stop.</summary>
    private static readonly TimeSpan DrainGracePeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Graceful shutdown: stops the timer, stops accepting IPC, drains the worker pool (every accepted
    /// Job finishes), disposes the watchers, and flushes/disposes the durable journal/audit/log.
    /// </summary>
    /// <remarks>
    /// The drain is bounded: it awaits <see cref="WorkerPool.DrainAsync"/> but if
    /// <paramref name="cancellationToken"/> trips or the <see cref="DrainGracePeriod"/> elapses (a wedged
    /// synchronous handler), it falls back to <see cref="WorkerPool.StopAsync"/> which cancels in-flight
    /// work — so <c>systemctl stop</c> / Ctrl+C always completes rather than hanging forever. When no
    /// cancellation/timeout occurs, the full drain finishes normally.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started_flag)
            return;
        _started_flag = false;

        if (_tickTimer is not null)
        {
            await _tickTimer.DisposeAsync().ConfigureAwait(false);
            _tickTimer = null;
        }

        // Stop accepting new connections/work, then drain in-flight + queued Jobs to completion.
        if (_ipc is not null)
            await _ipc.StopAsync().ConfigureAwait(false);

        if (_pool is not null)
            await DrainPoolBoundedAsync(_pool, cancellationToken).ConfigureAwait(false);

        foreach (SourceWatcher watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();

        _tray.Dispose();

        // Dispose the IPC transport (releases the pipe / cleans the socket file) after the accept loop
        // and connections have wound down.
        if (_ipc is not null)
        {
            await _ipc.DisposeAsync().ConfigureAwait(false);
            _ipc = null;
        }

        LogLine(LogSeverity.Info, "STOPPED", "FileManager service stopped.");

        // Flush + dispose the durable files last (after the pool drained, so the final records land).
        _journal?.Dispose();
        _audit?.Dispose();
        _log?.Dispose();
    }

    // Awaits a full graceful drain, but never blocks shutdown forever: if the caller's token trips or
    // the grace period elapses (e.g. a wedged synchronous handler), it cancels in-flight work via
    // WorkerPool.StopAsync so shutdown always completes. On the normal path the drain finishes first and
    // StopAsync is never reached.
    private async Task DrainPoolBoundedAsync(WorkerPool pool, CancellationToken cancellationToken)
    {
        Task drain = pool.DrainAsync();

        using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        graceCts.CancelAfter(DrainGracePeriod);

        Task trip = Task.Delay(Timeout.Infinite, graceCts.Token);
        Task winner = await Task.WhenAny(drain, trip).ConfigureAwait(false);

        if (winner == drain)
        {
            await drain.ConfigureAwait(false); // observe completion (and any fault).
            return;
        }

        // Token tripped or grace elapsed — fall back to a hard stop that cancels in-flight handlers.
        LogLine(LogSeverity.Failure, "DRAIN_TIMEOUT",
            "Graceful drain did not complete in time; cancelling in-flight Jobs to finish shutdown.");
        await pool.StopAsync().ConfigureAwait(false);
    }

    // ---- IEngineFacade ----

    /// <inheritdoc/>
    public SubmitPayloadResult Submit(SubmitPayload payload)
    {
        if (_payloads is null)
            return SubmitPayloadResult.Rejected("Service not started.");

        // The enqueue count is accounted inside PayloadQueue (via the onEnqueued callback wired in
        // StartAsync) so IPC, watcher, and scheduler submissions all increment _enqueued uniformly.
        return _payloads.Submit(payload);
    }

    /// <inheritdoc/>
    public EngineState GetState()
    {
        long enqueued = Interlocked.Read(ref _enqueued);
        long started = Interlocked.Read(ref _started);
        long inFlight = Interlocked.Read(ref _inFlight);
        long queued = enqueued - started;
        return new EngineState(
            QueuedCount: (int)Math.Max(0, queued),
            InFlightCount: (int)Math.Max(0, inFlight),
            WorkerCount: _pool?.MaxWorkers ?? 0,
            ProfileCount: _profiles.Count,
            ClosedCount: Interlocked.Read(ref _closed),
            SkippedCount: Interlocked.Read(ref _skipped),
            FailedCount: Interlocked.Read(ref _failed));
    }

    /// <inheritdoc/>
    public ProfileList ListProfiles()
    {
        IReadOnlyList<Profile> snapshot = _profiles;
        var summaries = snapshot
            .Select(p => new ProfileSummary(p.ProfileId, p.Name, p.Active))
            .ToList();
        return new ProfileList(summaries);
    }

    /// <inheritdoc/>
    public ReloadResult ReloadProfiles()
    {
        IReadOnlyList<string> errors = ReloadProfilesInternal();
        return new ReloadResult(_profiles.Count, errors);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Resolves the Profile (by <see cref="DryRunRequest.ProfileId"/>, else by matching the request path
    /// to a Profile's owning Source), enumerates candidate files under the path read-only (respecting
    /// <see cref="DryRunRequest.Recursive"/> for a directory), runs the side-effect-free
    /// <see cref="FileManager.Core.Simulation.DryRunEngine"/>, and maps the domain report onto the wire
    /// DTO. Reads only — it writes, moves, and deletes nothing. A resolution miss returns an empty,
    /// not-implemented-marked report carrying the reason rather than throwing.
    /// </remarks>
    public DryRunReport DryRun(DryRunRequest request)
    {
        IReadOnlyList<Profile> snapshot = _profiles;

        Profile? profile = ResolveDryRunProfile(snapshot, request);
        if (profile is null)
            return DryRunReport.Empty(request.ProfileId ?? string.Empty,
                "No active Profile matches the requested path/ProfileId.");

        IReadOnlyList<string> candidates = EnumerateDryRunCandidates(request);

        string trashRoot = _trashDirectory.Length > 0
            ? _trashDirectory
            : Path.Combine(_configDir, "trash");
        var engine = new FileManager.Core.Simulation.DryRunEngine(_files, trashRoot);
        FileManager.Core.Simulation.DryRunReport domain =
            engine.Simulate(profile, candidates, _clock.GetUtcNow());

        return DryRunReportMapper.ToWire(domain);
    }

    // Resolves the Profile to preview under: the explicit ProfileId when supplied (and active/loaded),
    // otherwise the active Profile whose Source root owns the request path (longest match wins).
    private static Profile? ResolveDryRunProfile(IReadOnlyList<Profile> profiles, DryRunRequest request)
    {
        if (request.ProfileId is { Length: > 0 } id)
            return profiles.FirstOrDefault(p => string.Equals(p.ProfileId, id, StringComparison.Ordinal));

        string path = PathNormalizer.Normalize(request.Path);
        Profile? best = null;
        int bestRootLength = -1;
        foreach (Profile profile in profiles)
        {
            foreach (SourceSpec source in profile.Sources)
            {
                if (!PathNormalizer.IsUnder(source.Path, path))
                    continue;
                int rootLength = PathNormalizer.Normalize(source.Path).Length;
                if (rootLength > bestRootLength)
                {
                    best = profile;
                    bestRootLength = rootLength;
                }
            }
        }

        return best;
    }

    // Enumerates the candidate source files under the request path read-only: a single file when the
    // path is a file, otherwise the directory's files (recursively when requested). Never mutates.
    private IReadOnlyList<string> EnumerateDryRunCandidates(DryRunRequest request)
    {
        if (_files.FileExists(request.Path))
            return new[] { request.Path };

        if (_files.DirectoryExists(request.Path))
            return _files.EnumerateFiles(request.Path, request.Recursive).ToList();

        return Array.Empty<string>();
    }

    // Loads Profiles from disk under the host's config dir, swaps the active snapshot to the valid +
    // active ones, and returns per-file error strings. Used at startup and by the reload IPC.
    private IReadOnlyList<string> ReloadProfilesInternal()
    {
        string profilesDir = Path.Combine(_configDir.Length == 0
            ? (_options.ConfigDirectory ?? ConfigPaths.GetConfigDirectory())
            : _configDir, ConfigPaths.ProfilesFolderName);

        IReadOnlyList<ProfileLoadResult> loaded = ProfileStore.LoadAllFrom(profilesDir);
        var errors = new List<string>();
        var active = new List<Profile>();
        foreach (ProfileLoadResult result in loaded)
        {
            if (result.IsValid && result.Profile!.Active)
                active.Add(result.Profile);
            else if (!result.IsValid)
                errors.Add($"{result.FilePath}: {string.Join("; ", result.Validation.Errors.Select(e => e.Message))}");
        }

        lock (_stateGate)
            _profiles = active;
        return errors;
    }

    private void LogLine(LogSeverity severity, string code, string message) =>
        _log?.Log(new JobLogEntry { Severity = severity, Code = code, JobId = string.Empty, Message = message });

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_pool is not null)
            await _pool.DisposeAsync().ConfigureAwait(false);
    }
}
