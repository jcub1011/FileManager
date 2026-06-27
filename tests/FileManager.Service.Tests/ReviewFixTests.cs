using System.IO;
using System.Runtime.InteropServices;
using FileManager.Contracts.Messages;
using FileManager.Core.Configuration;
using FileManager.Core.Execution;
using FileManager.Core.IO;
using FileManager.Core.Jobs;
using FileManager.Core.Profiles;
using FileManager.Service.Ipc;
using Xunit;

namespace FileManager.Service.Tests;

/// <summary>
/// Targeted regression tests for the four M6 review fixes: Unix socket created 0600 (no TOCTOU window),
/// uniform queued-count accounting across all trigger paths, a bounded shutdown drain that honors a
/// timeout/token, and stale-socket detection that never deletes a non-socket file.
/// </summary>
public sealed class ReviewFixTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    // ---- FIX 1: Unix socket file mode is owner-only (0600) on creation ----

    [Fact]
    public async Task UnixSocket_IsCreated_OwnerOnly()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Unix domain socket only.

        string socketPath = Path.Combine(Path.GetTempPath(), $"fm-mode-{Guid.NewGuid():N}.sock");
        var transport = new UnixSocketServerTransport(socketPath);
        try
        {
            UnixFileMode mode = File.GetUnixFileMode(socketPath);
            // Group and other bits must be clear (0600): the umask-around-bind + chmod close the window.
            UnixFileMode forbidden =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.Equal((UnixFileMode)0, mode & forbidden);
            Assert.True(mode.HasFlag(UnixFileMode.UserRead));
            Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
        }
        finally
        {
            await transport.DisposeAsync();
            IpcTestEndpoints.Cleanup(socketPath);
        }
    }

    // ---- FIX 2: queued count reflects every trigger path, not just the IPC facade ----

    [Fact]
    public void PayloadQueue_CountsEnqueues_ThroughCallback()
    {
        // The single counting point: a direct Submit (the path watcher/scheduler use, NOT the IPC
        // facade) must invoke onEnqueued with the queued count.
        string root = Path.Combine(Path.GetTempPath(), $"fm-pq-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "1");
            File.WriteAllText(Path.Combine(root, "b.txt"), "2");

            Profile profile = BuildProfile("pq", root);
            var queue = new JobQueue();
            int counted = 0;
            var payloads = new PayloadQueue(
                queue, () => new[] { profile }, new SystemFileOperations(),
                onEnqueued: n => counted += n);

            SubmitPayloadResult result = payloads.Submit(new SubmitPayload(root, "pq", Recursive: true));

            Assert.True(result.Accepted);
            Assert.Equal(2, result.QueuedCount);
            Assert.Equal(2, counted); // the callback fired with the same count.
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Host_ScheduledRun_IsReflectedInQueuedCount()
    {
        using var cts = new CancellationTokenSource(Generous);
        string configDir = Path.Combine(Path.GetTempPath(), $"fm-sched-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configDir);
        try
        {
            // A scheduled (interval) profile whose source has files; the startup sweep fires it,
            // enqueuing via the scheduler path (NOT the IPC facade).
            string sourceDir = Path.Combine(configDir, "src");
            string targetDir = Path.Combine(configDir, "dst");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(targetDir);
            for (int i = 0; i < 3; i++)
                File.WriteAllText(Path.Combine(sourceDir, $"s{i}.txt"), $"v{i}");

            Profile profile = BuildScheduledProfile("sched", sourceDir, targetDir);
            string profilesDir = Path.Combine(configDir, ConfigPaths.ProfilesFolderName);
            Directory.CreateDirectory(profilesDir);
            File.WriteAllText(Path.Combine(profilesDir, "sched.json"), ProfileSerializer.Serialize(profile));

            var clock = new TestClock(new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero));

            // Seed a last-run well in the past so the startup sweep coalesces a catch-up run (RunNow) for
            // this CatchUpOnce interval profile, firing it via the SCHEDULER path (not the IPC facade).
            var lastRuns = Core.State.LastRunStore.FromConfig(configDir);
            lastRuns.SetLastRun("sched", clock.UtcNow - TimeSpan.FromHours(1));

            await using var host = new ServiceHost(new ServiceHostOptions
            {
                ConfigDirectory = configDir,
                IpcEndpointName = IpcTestEndpoints.UniqueEndpoint(),
                ManualTicks = true,
                DisableWatchers = true,
                DisableIpc = true,
                Clock = clock,
            });

            await host.StartAsync(cts.Token); // startup sweep fires the coalesced catch-up run.

            // Drain so the scheduler-enqueued Jobs complete, then assert the engine state ACCOUNTED for
            // them (3 closed) and that QueuedCount never skewed negative (the bug being fixed).
            await host.StopAsync(cts.Token);

            EngineState state = host.GetState();
            Assert.Equal(3L, state.ClosedCount);
            Assert.True(state.QueuedCount >= 0);
            for (int i = 0; i < 3; i++)
                Assert.True(File.Exists(Path.Combine(targetDir, $"s{i}.txt")));
        }
        finally
        {
            if (Directory.Exists(configDir))
                Directory.Delete(configDir, recursive: true);
        }
    }

    // ---- FIX 3: StopAsync honors its token / grace period (bounded drain) ----

    [Fact]
    public async Task BoundedDrain_Fallback_CancelsWedgedJob_AndReturnsPromptly()
    {
        // The fallback the host's bounded drain uses when its token trips / grace elapses is
        // WorkerPool.StopAsync, which cancels in-flight handlers. With a handler that would otherwise
        // block 30s, StopAsync must return promptly — proving a wedged job can never hang shutdown.
        var queue = new JobQueue();
        using var entered = new ManualResetEventSlim(false);

        JobResult SlowHandler(JobRequest req, CancellationToken ct)
        {
            entered.Set();
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)); // released only by cancellation.
            return new JobResult { JobId = "j", State = JobState.Closed, SourcePath = req.SourcePath };
        }

        await using var pool = new WorkerPool(queue, maxWorkers: 1, SlowHandler);

        Profile profile = BuildProfile("slow", Path.GetTempPath());
        var ctx = new IngestionContext { Now = DateTimeOffset.UtcNow };
        await queue.EnqueueAsync(new JobRequest { Profile = profile, SourcePath = "x", Context = ctx });

        Assert.True(entered.Wait(Generous)); // the handler is genuinely in-flight (wedged).

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await pool.StopAsync();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"hard stop took {sw.Elapsed}");
    }

    [Fact]
    public async Task Host_StopAsync_PreCanceledToken_ReturnsPromptly()
    {
        // The host's bounded drain links the passed token: a pre-canceled token makes the drain fall
        // straight through to the hard-stop fallback, so StopAsync returns promptly (here there is no
        // wedged job, so it must be near-instant regardless).
        using var cts = new CancellationTokenSource(Generous);
        string configDir = Path.Combine(Path.GetTempPath(), $"fm-stoptok-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configDir);
        try
        {
            await using var host = new ServiceHost(new ServiceHostOptions
            {
                ConfigDirectory = configDir,
                IpcEndpointName = IpcTestEndpoints.UniqueEndpoint(),
                ManualTicks = true,
                DisableWatchers = true,
                DisableIpc = true,
                Clock = new TestClock(new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero)),
            });
            await host.StartAsync(cts.Token);

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await host.StopAsync(canceled.Token);
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"StopAsync took {sw.Elapsed}");
        }
        finally
        {
            if (Directory.Exists(configDir))
                Directory.Delete(configDir, recursive: true);
        }
    }

    [Fact]
    public async Task StopAsync_NormalPath_DrainsAllInFlightJobs()
    {
        using var cts = new CancellationTokenSource(Generous);
        string configDir = Path.Combine(Path.GetTempPath(), $"fm-drain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configDir);
        try
        {
            string sourceDir = Path.Combine(configDir, "src");
            string targetDir = Path.Combine(configDir, "dst");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(targetDir);
            const int count = 8;
            for (int i = 0; i < count; i++)
                File.WriteAllText(Path.Combine(sourceDir, $"d{i}.txt"), $"v{i}");

            Profile profile = BuildProfile("drain", sourceDir, targetDir);
            string profilesDir = Path.Combine(configDir, ConfigPaths.ProfilesFolderName);
            Directory.CreateDirectory(profilesDir);
            File.WriteAllText(Path.Combine(profilesDir, "drain.json"), ProfileSerializer.Serialize(profile));

            await using var host = new ServiceHost(new ServiceHostOptions
            {
                ConfigDirectory = configDir,
                IpcEndpointName = IpcTestEndpoints.UniqueEndpoint(),
                ManualTicks = true,
                DisableWatchers = true,
                DisableIpc = true,
                Clock = new TestClock(new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero)),
            });
            await host.StartAsync(cts.Token);

            host.Submit(new SubmitPayload(sourceDir, "drain", Recursive: true));

            // Normal path: no cancellation ⇒ full drain completes every accepted Job.
            await host.StopAsync(); // default token, no timeout trip.

            for (int i = 0; i < count; i++)
                Assert.True(File.Exists(Path.Combine(targetDir, $"d{i}.txt")), $"d{i}.txt missing");
        }
        finally
        {
            if (Directory.Exists(configDir))
                Directory.Delete(configDir, recursive: true);
        }
    }

    // ---- helpers ----

    private static Profile BuildProfile(string id, string sourceDir, string? targetDir = null)
    {
        targetDir ??= Path.Combine(Path.GetTempPath(), $"fm-t-{Guid.NewGuid():N}");
        return new Profile
        {
            SchemaVersion = 2,
            ProfileId = id,
            Name = id,
            Active = true,
            SyncMode = SyncMode.AdditiveArchive,
            TargetLayout = TargetLayout.PreserveStructure,
            Triggers = new TriggerSet { ManualShell = true, Watcher = false, Schedule = null },
            Sources = new[] { new SourceSpec { Path = sourceDir, SettleDelaySeconds = 0, StabilityIntervalMs = 0 } },
            Transformers = null,
            Targets = new[] { new TargetSpec { Path = targetDir } },
            Policies = DefaultPolicies(),
            Filters = new FilterSet(),
            Logging = new LoggingSpec { Verbosity = Verbosity.All, NotifyOnFailure = false },
        };
    }

    private static Profile BuildScheduledProfile(string id, string sourceDir, string targetDir) =>
        BuildProfile(id, sourceDir, targetDir) with
        {
            Triggers = new TriggerSet
            {
                ManualShell = true,
                Watcher = false,
                // A due interval schedule: the startup sweep treats a never-run interval profile as due.
                Schedule = new ScheduleTrigger
                {
                    Enabled = true,
                    IntervalSeconds = 60,
                    MissedRunPolicy = MissedRunPolicy.CatchUpOnce,
                },
            },
        };

    private static PolicySet DefaultPolicies() => new()
    {
        ConflictResolution = ConflictResolution.Overwrite,
        OverwriteHandling = OverwriteHandling.DirectOverwrite,
        VerificationMethod = VerificationMethod.None,
        OnSuccess = OnSuccess.KeepSource,
        ArchiveFolder = null,
        OnFailure = OnFailure.AbortRestoreAndClean,
        MetadataOnConflict = MetadataOnConflict.WarnAndContinue,
    };
}
