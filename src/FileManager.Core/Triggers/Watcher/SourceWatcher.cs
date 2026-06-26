using FileManager.Core.Logging;

namespace FileManager.Core.Triggers.Watcher;

/// <summary>
/// Watches one Source root and emits the paths of files that are <em>ready</em> to ingest (§3.2.1):
/// a file is ready only when BOTH (a) no change events have arrived for <c>SettleDelaySeconds</c> (the
/// debounce, owned here) AND (b) the <see cref="ReadinessProbe"/> succeeds. Ready paths are handed to a
/// callback (the M6 host enqueues them onto the <c>JobQueue</c>).
/// </summary>
/// <remarks>
/// <para><b>Determinism.</b> The debounce clock is a <see cref="TimeProvider"/> and the underlying
/// filesystem events come through the <see cref="ISourceFileWatcher"/> seam, so a test feeds synthetic
/// create/change/overflow events and advances a fake clock — no real wall-clock sleeps or real
/// filesystem races. <see cref="Tick"/> is the deterministic pump: it promotes files whose settle
/// window has elapsed, probes them, and emits the ready ones.</para>
/// <para><b>Overflow recovery (§11).</b> A watcher <see cref="ISourceFileWatcher.Error"/> (Windows
/// buffer overflow / Linux watch-limit) routes through <see cref="WatcherScaleManager"/> to a bounded
/// <see cref="RescanFallback"/> over this root; every file found is re-offered exactly like a live
/// event, so files created during the dropped-event gap are not missed.</para>
/// <para><b>Network caveat.</b> When the probe relaxes to size-stability for a network Source, the
/// per-file caveat is logged once.</para>
/// </remarks>
public sealed class SourceWatcher : IDisposable
{
    private readonly ISourceFileWatcher _watcher;
    private readonly ReadinessProbe _probe;
    private readonly WatcherScaleManager _scale;
    private readonly RescanFallback _rescan;
    private readonly TimeProvider _clock;
    private readonly ILogSink _log;
    private readonly int _settleDelaySeconds;
    private readonly int _stabilityIntervalMs;
    private readonly Action<string> _onReady;

    // Per-file debounce state: the last event time. Guarded by _gate (events may arrive on a watcher
    // thread while Tick runs on the host's pump thread).
    private readonly Dictionary<string, DateTimeOffset> _pending;
    private readonly object _gate = new();

    /// <summary>
    /// Creates a watcher over <paramref name="watcher"/> (the FileSystemWatcher seam), emitting ready
    /// file paths to <paramref name="onReady"/>. <paramref name="settleDelaySeconds"/> /
    /// <paramref name="stabilityIntervalMs"/> are the per-Source settle policy (typically from the
    /// owning <c>SourceSpec</c>). <paramref name="clock"/> defaults to <see cref="TimeProvider.System"/>.
    /// </summary>
    public SourceWatcher(
        ISourceFileWatcher watcher,
        ReadinessProbe probe,
        WatcherScaleManager scale,
        RescanFallback rescan,
        ILogSink log,
        int settleDelaySeconds,
        int stabilityIntervalMs,
        Action<string> onReady,
        TimeProvider? clock = null)
    {
        _watcher = watcher;
        _probe = probe;
        _scale = scale;
        _rescan = rescan;
        _log = log;
        _settleDelaySeconds = Math.Max(0, settleDelaySeconds);
        _stabilityIntervalMs = Math.Max(0, stabilityIntervalMs);
        _onReady = onReady;
        _clock = clock ?? TimeProvider.System;
        _pending = new Dictionary<string, DateTimeOffset>(StringComparerForRoot());

        _watcher.Changed += OnChanged;
        _watcher.Error += OnError;
    }

    private static StringComparer StringComparerForRoot() =>
        IO.PathNormalizer.Comparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>The Source root this watcher covers.</summary>
    public string Root => _watcher.Root;

    /// <summary>Starts delivering events from the underlying watcher.</summary>
    public void Start() => _watcher.Start();

    private void OnChanged(WatcherChange change)
    {
        // (a) Debounce: (re)stamp the file's last-event time. The settle window restarts on every event,
        // so a still-being-written file that keeps emitting Change events never settles until writes stop.
        lock (_gate)
            _pending[IO.PathNormalizer.Normalize(change.FullPath)] = _clock.GetUtcNow();
    }

    private void OnError(Exception error)
    {
        // §11 recovery: the OS dropped events. Rescan this root (bounded) and re-offer every file as if
        // a fresh event had arrived, so files created during the gap are caught. Stamp them at "now" so
        // they still observe the full settle window.
        _log.Log(new JobLogEntry
        {
            Severity = LogSeverity.Failure,
            Code = "WATCHER_OVERFLOW",
            JobId = string.Empty,
            Message = $"{Root}: watcher buffer overflow / watch-limit ({error.Message}); rescanning to recover missed files.",
        });

        IReadOnlyList<string> found = _scale.RecoverByRescan(Root, _rescan);
        DateTimeOffset now = _clock.GetUtcNow();
        lock (_gate)
        {
            foreach (string path in found)
                _pending[path] = now;
        }
    }

    /// <summary>
    /// The deterministic pump: promotes every pending file whose settle window has elapsed as of
    /// <paramref name="now"/>, probes it, and emits the ready ones via the callback. A file that has
    /// settled but fails the probe (still open / size changing) is re-stamped at <paramref name="now"/>
    /// so it is re-evaluated after another settle window rather than being emitted prematurely or
    /// dropped. Returns the paths emitted on this tick (for tests / diagnostics).
    /// </summary>
    public IReadOnlyList<string> Tick(DateTimeOffset now)
    {
        // Snapshot each due path WITH the settle timestamp it had when found due. The probe runs outside
        // the lock (it may sample/sleep), so a fresh Changed event can re-stamp a path meanwhile. We
        // guard the emit/remove with a compare-and-remove against that captured timestamp so a file
        // re-touched during the probe is NOT emitted — it has resumed activity and must re-settle.
        List<(string Path, DateTimeOffset Settle)> due;
        lock (_gate)
        {
            due = new List<(string, DateTimeOffset)>();
            foreach ((string path, DateTimeOffset lastEvent) in _pending)
            {
                if ((now - lastEvent).TotalSeconds >= _settleDelaySeconds)
                    due.Add((path, lastEvent));
            }
        }

        var emitted = new List<string>();
        foreach ((string path, DateTimeOffset settledAt) in due)
        {
            ReadinessResult result = _probe.Probe(path, _stabilityIntervalMs);
            if (result.NetworkCaveat)
            {
                _log.Log(new JobLogEntry
                {
                    Severity = LogSeverity.Info,
                    Code = "WATCHER_NETWORK_CAVEAT",
                    JobId = string.Empty,
                    Message = $"{path}: network Source — readiness relaxed to size-stability only.",
                });
            }

            bool emit = false;
            lock (_gate)
            {
                // A concurrent Changed event during the probe re-stamps _pending[path] to a newer time;
                // if the stored timestamp no longer matches what we probed against, the file became
                // active again — leave it pending to re-settle rather than emitting a stale "ready".
                if (!_pending.TryGetValue(path, out DateTimeOffset current) || current != settledAt)
                    continue;

                if (result.Ready)
                {
                    _pending.Remove(path);
                    emit = true;
                }
                else
                {
                    // Not ready yet (still open / size changing) — restart the settle window so we
                    // re-probe later instead of emitting prematurely.
                    _pending[path] = now;
                }
            }

            if (emit)
            {
                emitted.Add(path);
                _onReady(path);
            }
        }

        return emitted;
    }

    /// <summary>The number of files currently awaiting settle/readiness (diagnostic).</summary>
    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    public void Dispose()
    {
        _watcher.Changed -= OnChanged;
        _watcher.Error -= OnError;
        _watcher.Dispose();
    }
}
