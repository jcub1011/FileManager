using System.IO;
using FileManager.Contracts.Messages;
using FileManager.Core.Configuration;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Service.Tests;

/// <summary>
/// End-to-end <see cref="ServiceHost"/> tests for the §3.2 always-prompt / never-auto-run invariant and
/// MaxDepth folder recursion: a manual submit pushes a <see cref="ManualInvocationPending"/> and queues
/// NOTHING until resolved; a non-manual submit still auto-runs; and resolving a folder manual invocation
/// processes only files within the Profile's MaxDepth (reusing the engine's per-file screening — no
/// duplicated depth logic). All against an isolated temp config dir; no display server, no OS registration.
/// </summary>
public sealed class ManualInvocationHostTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);
    private readonly string _configDir;

    public ManualInvocationHostTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "fm-manual-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch (IOException) { }
    }

    private (string SourceDir, string TargetDir) WriteProfile(string profileId, int? maxDepth = null)
    {
        string sourceDir = Path.Combine(_configDir, profileId, "src");
        string targetDir = Path.Combine(_configDir, profileId, "dst");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        var profile = new Profile
        {
            SchemaVersion = 2,
            ProfileId = profileId,
            Name = profileId,
            Active = true,
            SyncMode = SyncMode.AdditiveArchive,
            TargetLayout = TargetLayout.PreserveStructure,
            Triggers = new TriggerSet { ManualShell = true, Watcher = false, Schedule = null },
            Sources = new[] { new SourceSpec { Path = sourceDir, SettleDelaySeconds = 0, StabilityIntervalMs = 0 } },
            Transformers = null,
            Targets = new[] { new TargetSpec { Path = targetDir } },
            Policies = new PolicySet
            {
                ConflictResolution = ConflictResolution.Overwrite,
                OverwriteHandling = OverwriteHandling.DirectOverwrite,
                VerificationMethod = VerificationMethod.None,
                OnSuccess = OnSuccess.KeepSource,
                ArchiveFolder = null,
                OnFailure = OnFailure.AbortRestoreAndClean,
                MetadataOnConflict = MetadataOnConflict.WarnAndContinue,
            },
            Filters = new FilterSet { MaxDepth = maxDepth },
            Logging = new LoggingSpec { Verbosity = Verbosity.All, NotifyOnFailure = false },
        };

        string profilesDir = Path.Combine(_configDir, ConfigPaths.ProfilesFolderName);
        Directory.CreateDirectory(profilesDir);
        File.WriteAllText(Path.Combine(profilesDir, profileId + ".json"), ProfileSerializer.Serialize(profile));
        return (sourceDir, targetDir);
    }

    private ServiceHost NewHost(string endpoint) =>
        new(new ServiceHostOptions
        {
            ConfigDirectory = _configDir,
            IpcEndpointName = endpoint,
            ManualTicks = true,
            DisableWatchers = true,
            DisableIpc = false,
            Clock = new TestClock(new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero)),
        });

    [Fact]
    public async Task ManualSubmit_PushesPending_QueuesNothing_ThenResolveRuns()
    {
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        (string sourceDir, string targetDir) = WriteProfile("man1");
        File.WriteAllText(Path.Combine(sourceDir, "a.txt"), "hello");

        await using ServiceHost host = NewHost(endpoint);
        try
        {
            await host.StartAsync(cts.Token);

            // A subscriber observes the ManualInvocationPending push.
            await using Stream subscriber = await IpcTestEndpoints.ConnectAsync(endpoint, Generous, cts.Token);
            await Framing.WriteMessageAsync(subscriber,
                ContractsSerializer.SerializeToUtf8Bytes(IpcMessage.ForSubscribe()), cts.Token);

            await using Stream client = await IpcTestEndpoints.ConnectAsync(endpoint, Generous, cts.Token);
            IpcMessage submitResponse = await IpcTestEndpoints.RequestAsync(
                client,
                IpcMessage.ForSubmit(new SubmitPayload(sourceDir, null, true, IsManual: true)),
                cts.Token);

            // Accepted-but-pending: nothing queued, an invocation id returned (never auto-run).
            Assert.True(submitResponse.SubmitResult!.Accepted);
            Assert.Equal(0, submitResponse.SubmitResult.QueuedCount);
            Assert.NotNull(submitResponse.SubmitResult.PendingInvocationId);

            // The subscriber receives the pending push with the matching profile.
            byte[]? frame = await Framing.ReadMessageAsync(subscriber, cts.Token);
            Assert.NotNull(frame);
            Assert.True(ContractsSerializer.TryDeserialize(frame, out IpcMessage? push, out _));
            Assert.Equal(MessageKind.ManualInvocationPending, push!.Kind);
            ManualInvocationPending pending = push.ManualInvocationPending!;
            Assert.Contains(pending.Matches, m => m.ProfileId == "man1");

            // Nothing ran yet — target empty, no closed jobs.
            Assert.False(File.Exists(Path.Combine(targetDir, "a.txt")));

            // Resolve with the chosen profile → the job runs.
            IpcMessage resolveResponse = await IpcTestEndpoints.RequestAsync(
                client,
                IpcMessage.ForResolveManualInvocation(new ResolveManualInvocation(pending.InvocationId, "man1")),
                cts.Token);
            Assert.True(resolveResponse.SubmitResult!.Accepted);
            Assert.Equal(1, resolveResponse.SubmitResult.QueuedCount);

            // The terminal Job event arrives (the chosen profile actually processed the file).
            byte[]? eventFrame = await Framing.ReadMessageAsync(subscriber, cts.Token);
            Assert.NotNull(eventFrame);
            Assert.True(ContractsSerializer.TryDeserialize(eventFrame, out IpcMessage? evt, out _));
            Assert.Equal(MessageKind.Event, evt!.Kind);
            Assert.True(File.Exists(Path.Combine(targetDir, "a.txt")));
        }
        finally
        {
            await host.StopAsync();
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }

    [Fact]
    public async Task ManualSubmit_BeforeAnySubscriber_IsReplayedToLateSubscriber_ThenResolveRuns()
    {
        // BLOCKER-1 regression: a manual invocation is submitted with NO subscriber connected (the GUI is
        // still cold-starting). The live push has nowhere to go; delivery must survive via replay-on-
        // subscribe so the prompt is never lost.
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        (string sourceDir, string targetDir) = WriteProfile("late1");
        File.WriteAllText(Path.Combine(sourceDir, "a.txt"), "hello");

        await using ServiceHost host = NewHost(endpoint);
        try
        {
            await host.StartAsync(cts.Token);

            // Submit FIRST, with no subscriber present.
            await using Stream client = await IpcTestEndpoints.ConnectAsync(endpoint, Generous, cts.Token);
            IpcMessage submitResponse = await IpcTestEndpoints.RequestAsync(
                client,
                IpcMessage.ForSubmit(new SubmitPayload(sourceDir, null, true, IsManual: true)),
                cts.Token);
            Assert.True(submitResponse.SubmitResult!.Accepted);
            Assert.NotNull(submitResponse.SubmitResult.PendingInvocationId);

            // NOW subscribe (the late, cold-started GUI). The pending must be replayed to it.
            await using Stream subscriber = await IpcTestEndpoints.ConnectAsync(endpoint, Generous, cts.Token);
            await Framing.WriteMessageAsync(subscriber,
                ContractsSerializer.SerializeToUtf8Bytes(IpcMessage.ForSubscribe()), cts.Token);

            byte[]? frame = await Framing.ReadMessageAsync(subscriber, cts.Token);
            Assert.NotNull(frame);
            Assert.True(ContractsSerializer.TryDeserialize(frame, out IpcMessage? push, out _));
            Assert.Equal(MessageKind.ManualInvocationPending, push!.Kind);
            ManualInvocationPending pending = push.ManualInvocationPending!;
            Assert.Equal(submitResponse.SubmitResult.PendingInvocationId, pending.InvocationId);
            Assert.Contains(pending.Matches, m => m.ProfileId == "late1");

            // Resolving the replayed pending runs the chosen profile (end-to-end proof it wasn't dropped).
            IpcMessage resolveResponse = await IpcTestEndpoints.RequestAsync(
                client,
                IpcMessage.ForResolveManualInvocation(new ResolveManualInvocation(pending.InvocationId, "late1")),
                cts.Token);
            Assert.True(resolveResponse.SubmitResult!.Accepted);
            Assert.Equal(1, resolveResponse.SubmitResult.QueuedCount);

            byte[]? eventFrame = await Framing.ReadMessageAsync(subscriber, cts.Token);
            Assert.NotNull(eventFrame);
            Assert.True(ContractsSerializer.TryDeserialize(eventFrame, out IpcMessage? evt, out _));
            Assert.Equal(MessageKind.Event, evt!.Kind);
            Assert.True(File.Exists(Path.Combine(targetDir, "a.txt")));
        }
        finally
        {
            await host.StopAsync();
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }

    [Fact]
    public async Task ResolvedManualInvocation_IsNotReplayedToNewSubscriber()
    {
        // Once resolved, a pending must NOT linger for replay (no duplicate prompt on a later subscribe).
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        (string sourceDir, _) = WriteProfile("res1");
        File.WriteAllText(Path.Combine(sourceDir, "a.txt"), "hello");

        await using ServiceHost host = NewHost(endpoint);
        try
        {
            await host.StartAsync(cts.Token);

            SubmitPayloadResult submit = host.Submit(new SubmitPayload(sourceDir, null, true, IsManual: true));
            // Cancel it (discard).
            host.ResolveManualInvocation(new ResolveManualInvocation(submit.PendingInvocationId!, null));

            Assert.Empty(host.GetUnresolvedManualInvocations());
        }
        finally
        {
            await host.StopAsync();
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }

    [Fact]
    public async Task NonManualSubmit_StillAutoRuns()
    {
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        (string sourceDir, string targetDir) = WriteProfile("auto1");
        File.WriteAllText(Path.Combine(sourceDir, "a.txt"), "hello");

        await using ServiceHost host = NewHost(endpoint);
        try
        {
            await host.StartAsync(cts.Token);

            SubmitPayloadResult result = host.Submit(new SubmitPayload(sourceDir, "auto1", true, IsManual: false));

            Assert.True(result.Accepted);
            Assert.Equal(1, result.QueuedCount);          // auto-enqueued, not pending
            Assert.Null(result.PendingInvocationId);
        }
        finally
        {
            await host.StopAsync();
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }

    [Fact]
    public async Task ManualFolderInvocation_HonorsMaxDepth_ViaEngineScreening()
    {
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        // MaxDepth = 0: only files directly under the source root are processed; deeper ones are screened.
        (string sourceDir, string targetDir) = WriteProfile("depth1", maxDepth: 0);

        File.WriteAllText(Path.Combine(sourceDir, "shallow.txt"), "shallow");
        string deepDir = Path.Combine(sourceDir, "sub");
        Directory.CreateDirectory(deepDir);
        File.WriteAllText(Path.Combine(deepDir, "deep.txt"), "deep");

        await using ServiceHost host = NewHost(endpoint);
        try
        {
            await host.StartAsync(cts.Token);

            // Manual folder invocation → resolve under the profile (recursive enumeration; the engine
            // screens each file by Depth via the same FilterEvaluator path every trigger uses — no
            // duplicate depth logic in the manual flow).
            SubmitPayloadResult submit = host.Submit(new SubmitPayload(sourceDir, null, true, IsManual: true));
            Assert.NotNull(submit.PendingInvocationId);

            SubmitPayloadResult resolved = host.ResolveManualInvocation(
                new ResolveManualInvocation(submit.PendingInvocationId!, "depth1"));
            Assert.True(resolved.Accepted);
            // Both files are enumerated + enqueued; the depth screen is applied per-file at process time.
            Assert.Equal(2, resolved.QueuedCount);

            // Drain: stop the host so the pool finishes processing the queued jobs.
            await host.StopAsync(cts.Token);

            // Engine screening (FilterEvaluator: candidate.Depth > MaxDepth → SKIP) means only the shallow
            // file was copied; the deeper one was screened out, never written.
            Assert.True(File.Exists(Path.Combine(targetDir, "shallow.txt")));
            Assert.False(File.Exists(Path.Combine(targetDir, "sub", "deep.txt")));
        }
        finally
        {
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }
}
