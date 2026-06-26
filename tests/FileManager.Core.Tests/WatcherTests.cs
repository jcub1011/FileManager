using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Logging;
using FileManager.Core.Triggers.Watcher;
using Xunit;

namespace FileManager.Core.Tests;

/// <summary>
/// Covers readiness (settle + probe), the network size-stability relaxation, and watcher
/// buffer-overflow recovery via rescan — all driven by seams, no real filesystem events or sleeps.
/// </summary>
public sealed class WatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- Readiness probe ----

    [Fact]
    public void Windows_StillOpenForWriting_NotReady()
    {
        var fp = new FakeReadinessFileProbe { ExclusiveOpenSucceeds = false };
        var probe = new ReadinessProbe(fp, treatAsWindows: true);

        ReadinessResult r = probe.Probe(@"C:\src\a.txt", stabilityIntervalMs: 0);

        Assert.False(r.Ready);
    }

    [Fact]
    public void Windows_ExclusiveOpenSucceeds_Ready()
    {
        var fp = new FakeReadinessFileProbe { ExclusiveOpenSucceeds = true };
        var probe = new ReadinessProbe(fp, treatAsWindows: true);

        Assert.True(probe.Probe(@"C:\src\a.txt", 0).Ready);
    }

    [Fact]
    public void Linux_SizeStillChanging_NotReady()
    {
        var fp = new FakeReadinessFileProbe { AdvisoryLocked = false };
        fp.SizeSamples.Enqueue(100);
        fp.SizeSamples.Enqueue(200); // grew between samples
        var probe = new ReadinessProbe(fp, treatAsWindows: false);

        Assert.False(probe.Probe("/src/a.txt", 10).Ready);
    }

    [Fact]
    public void Linux_StableSize_NotLocked_Ready()
    {
        var fp = new FakeReadinessFileProbe { AdvisoryLocked = false };
        fp.SizeSamples.Enqueue(500);
        fp.SizeSamples.Enqueue(500);
        var probe = new ReadinessProbe(fp, treatAsWindows: false);

        Assert.True(probe.Probe("/src/a.txt", 10).Ready);
    }

    [Fact]
    public void Linux_AdvisoryLocked_NotReady()
    {
        var fp = new FakeReadinessFileProbe { AdvisoryLocked = true };
        var probe = new ReadinessProbe(fp, treatAsWindows: false);

        Assert.False(probe.Probe("/src/a.txt", 10).Ready);
    }

    [Fact]
    public void NetworkSource_RelaxesToSizeStability_WithCaveat()
    {
        // Network path: exclusive-open is NOT consulted; only size stability, and the caveat is flagged.
        var fp = new FakeReadinessFileProbe { Network = true, ExclusiveOpenSucceeds = false };
        fp.SizeSamples.Enqueue(42);
        fp.SizeSamples.Enqueue(42);
        var probe = new ReadinessProbe(fp, treatAsWindows: true);

        ReadinessResult r = probe.Probe(@"\\server\share\a.txt", 10);

        Assert.True(r.Ready);
        Assert.True(r.NetworkCaveat);
    }

    // ---- SourceWatcher: settle + emit ----

    [Fact]
    public void File_NotEmitted_UntilSettleWindowElapses()
    {
        var fsw = new FakeSourceFileWatcher("/src");
        var fp = new FakeReadinessFileProbe { AdvisoryLocked = false };
        fp.DefaultSize = 10; // always stable
        var emitted = new List<string>();
        using var watcher = NewWatcher(fsw, fp, settleSeconds: 5, emitted, clock: new TestTimeProvider(T0));

        fsw.Raise(WatcherChangeKind.Created, "/src/a.txt");

        // Before the settle window: nothing emitted.
        Assert.Empty(watcher.Tick(T0.AddSeconds(3)));
        Assert.Empty(emitted);

        // After the settle window with a stable probe: emitted.
        var after = watcher.Tick(T0.AddSeconds(6));
        Assert.Single(after);
        Assert.Single(emitted);
    }

    [Fact]
    public void File_StillGrowing_NotEmitted_EvenAfterSettle()
    {
        var fsw = new FakeSourceFileWatcher("/src");
        var fp = new FakeReadinessFileProbe { AdvisoryLocked = false };
        fp.SizeSamples.Enqueue(100);
        fp.SizeSamples.Enqueue(200); // still growing on the probe
        var emitted = new List<string>();
        using var watcher = NewWatcher(fsw, fp, settleSeconds: 5, emitted, clock: new TestTimeProvider(T0));

        fsw.Raise(WatcherChangeKind.Created, "/src/a.txt");

        // Settle window elapsed but the probe fails ⇒ not emitted; the file stays pending for re-probe.
        Assert.Empty(watcher.Tick(T0.AddSeconds(6)));
        Assert.Empty(emitted);
        Assert.Equal(1, watcher.PendingCount);
    }

    [Fact]
    public void File_ReTouchedDuringProbe_NotEmitted_UntilItSettlesAgain()
    {
        var fsw = new FakeSourceFileWatcher("/src");
        var clock = new TestTimeProvider(T0);
        var fp = new FakeReadinessFileProbe { AdvisoryLocked = false };

        // Probe reports a STABLE size (would pass), but DURING the probe (the Wait between the two size
        // samples) a fresh Changed event arrives — the file resumed activity. The compare-and-remove
        // must then refuse to emit it on this tick and leave it pending to re-settle.
        fp.SizeSamples.Enqueue(100);
        fp.SizeSamples.Enqueue(100);
        fp.OnWait = () =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));             // time moves on
            fsw.Raise(WatcherChangeKind.Changed, "/src/a.txt"); // re-stamps _pending to the new "now"
        };

        var emitted = new List<string>();
        using var watcher = NewWatcher(fsw, fp, settleSeconds: 5, emitted, clock);

        fsw.Raise(WatcherChangeKind.Created, "/src/a.txt");

        // The settle window has elapsed for the ORIGINAL stamp, so the file is due and gets probed; but
        // the re-touch during the probe changes its stamp ⇒ NOT emitted, still pending.
        var due = watcher.Tick(T0.AddSeconds(6));
        Assert.Empty(due);
        Assert.Empty(emitted);
        Assert.Equal(1, watcher.PendingCount);

        // Once it settles again (no further events) and probes stable, it IS emitted.
        fp.SizeSamples.Enqueue(100);
        fp.SizeSamples.Enqueue(100);
        var due2 = watcher.Tick(T0.AddSeconds(20));
        Assert.Single(due2);
        Assert.Single(emitted);
    }

    // ---- Overflow recovery via rescan ----

    [Fact]
    public void BufferOverflow_TriggersRescan_CatchesMissedFiles()
    {
        using var dir = new TempDir("watch");
        string root = dir.MakeDir("S");
        // Two files exist on disk that the watcher "missed" during the overflow gap.
        string a = dir.WriteFile("S/a.txt", "aa");
        string b = dir.WriteFile("S/b.txt", "bb");

        var fsw = new FakeSourceFileWatcher(root);
        var fp = new FakeReadinessFileProbe { AdvisoryLocked = false };
        fp.DefaultSize = 2; // stable
        var emitted = new List<string>();
        var scale = new WatcherScaleManager();
        var rescan = new RescanFallback(new SystemFileOperations());
        var clock = new TestTimeProvider(T0);
        using var watcher = new SourceWatcher(
            fsw, new ReadinessProbe(fp, treatAsWindows: false), scale, rescan,
            new InMemoryLogSink(), settleDelaySeconds: 0, stabilityIntervalMs: 0, p => emitted.Add(p), clock);

        // Simulate the OS dropping events: the watcher never delivered create events for a/b.
        fsw.RaiseError(new InternalBufferOverflowException("overflow"));

        // With a zero settle window the rescanned files are immediately due.
        var due = watcher.Tick(T0);

        Assert.Equal(1, scale.RecoveryCount);
        Assert.Contains(PathNormalizer.Normalize(a), due);
        Assert.Contains(PathNormalizer.Normalize(b), due);
    }

    private static SourceWatcher NewWatcher(
        FakeSourceFileWatcher fsw, FakeReadinessFileProbe fp, int settleSeconds, List<string> emitted, TestTimeProvider clock) =>
        new(
            fsw,
            new ReadinessProbe(fp, treatAsWindows: false),
            new WatcherScaleManager(),
            new RescanFallback(new SystemFileOperations()),
            new InMemoryLogSink(),
            settleDelaySeconds: settleSeconds,
            stabilityIntervalMs: 0,
            onReady: emitted.Add,
            clock: clock);
}

/// <summary>A scriptable <see cref="IReadinessFileProbe"/> — no real filesystem.</summary>
internal sealed class FakeReadinessFileProbe : IReadinessFileProbe
{
    public bool Network { get; set; }
    public bool ExclusiveOpenSucceeds { get; set; } = true;
    public bool AdvisoryLocked { get; set; }
    public long DefaultSize { get; set; } = -1;
    public Queue<long> SizeSamples { get; } = new();

    /// <summary>Optional hook fired during <see cref="Wait"/> (between size samples), to simulate a
    /// concurrent event arriving mid-probe. Fires once then clears.</summary>
    public Action? OnWait { get; set; }

    public bool IsNetworkPath(string path) => Network;
    public bool TryOpenExclusive(string path) => ExclusiveOpenSucceeds;
    public bool IsAdvisoryLocked(string path) => AdvisoryLocked;
    public long GetSize(string path) => SizeSamples.Count > 0 ? SizeSamples.Dequeue() : DefaultSize;

    public void Wait(int milliseconds)
    {
        // No real sleep. Fire the one-shot hook to simulate an event landing during the probe.
        Action? hook = OnWait;
        OnWait = null;
        hook?.Invoke();
    }
}

/// <summary>A test <see cref="ISourceFileWatcher"/> driven by explicit Raise/RaiseError calls.</summary>
internal sealed class FakeSourceFileWatcher : ISourceFileWatcher
{
    public FakeSourceFileWatcher(string root) => Root = PathNormalizer.Normalize(root);

    public string Root { get; }
    public event Action<WatcherChange>? Changed;
    public event Action<Exception>? Error;

    public void Start() { }
    public void Raise(WatcherChangeKind kind, string fullPath) => Changed?.Invoke(new WatcherChange(kind, fullPath));
    public void RaiseError(Exception ex) => Error?.Invoke(ex);
    public void Dispose() { }
}
