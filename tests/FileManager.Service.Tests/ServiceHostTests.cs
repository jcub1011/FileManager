using System.IO;
using FileManager.Contracts.Messages;
using FileManager.Core.Configuration;
using FileManager.Core.Journal;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Service.Tests;

/// <summary>
/// End-to-end <see cref="ServiceHost"/> tests against an isolated temp config directory: a full IPC
/// round-trip through the host (submit → state reflects the queued/processed Job; subscriber sees the
/// event), and a clean-shutdown drain that processes every accepted Job and flushes the durable journal.
/// </summary>
public sealed class ServiceHostTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);
    private readonly string _configDir;

    public ServiceHostTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "fm-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_configDir))
                Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Writes a valid copy Profile (source → target, KeepSource) into the host's profiles directory.
    private (string SourceDir, string TargetDir) WriteCopyProfile(string profileId, bool watcher = false)
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
            Triggers = new TriggerSet { ManualShell = true, Watcher = watcher, Schedule = null },
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
            Filters = new FilterSet(),
            Logging = new LoggingSpec { Verbosity = Verbosity.All, NotifyOnFailure = false },
        };

        string profilesDir = Path.Combine(_configDir, ConfigPaths.ProfilesFolderName);
        Directory.CreateDirectory(profilesDir);
        File.WriteAllText(Path.Combine(profilesDir, profileId + ".json"), ProfileSerializer.Serialize(profile));
        return (sourceDir, targetDir);
    }

    private ServiceHost NewHost(string endpoint, bool disableIpc = false, bool disableWatchers = true) =>
        new(new ServiceHostOptions
        {
            ConfigDirectory = _configDir,
            IpcEndpointName = endpoint,
            ManualTicks = true,
            DisableWatchers = disableWatchers,
            DisableIpc = disableIpc,
            Clock = new TestClock(new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero)),
        });

    [Fact]
    public async Task FullHost_Submit_ReflectsInStateAndEventStream()
    {
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        (string sourceDir, string targetDir) = WriteCopyProfile("copy1");
        File.WriteAllText(Path.Combine(sourceDir, "a.txt"), "hello");

        await using ServiceHost host = NewHost(endpoint);
        try
        {
            await host.StartAsync(cts.Token);

            await using Stream client = await IpcTestEndpoints.ConnectAsync(endpoint, Generous, cts.Token);

            // Subscribe first on a SEPARATE connection so we observe the Job event.
            await using Stream subscriber = await IpcTestEndpoints.ConnectAsync(endpoint, Generous, cts.Token);
            await Framing.WriteMessageAsync(subscriber,
                ContractsSerializer.SerializeToUtf8Bytes(IpcMessage.ForSubscribe()), cts.Token);

            // Submit the file.
            IpcMessage submitResponse = await IpcTestEndpoints.RequestAsync(
                client, IpcMessage.ForSubmit(new SubmitPayload(Path.Combine(sourceDir, "a.txt"), "copy1", false)),
                cts.Token);
            Assert.True(submitResponse.SubmitResult!.Accepted);
            Assert.Equal(1, submitResponse.SubmitResult.QueuedCount);

            // The subscriber receives the terminal Job event (signal-based wait via the framed read).
            byte[]? eventFrame = await Framing.ReadMessageAsync(subscriber, cts.Token);
            Assert.NotNull(eventFrame);
            Assert.True(ContractsSerializer.TryDeserialize(eventFrame, out IpcMessage? evt, out _));
            Assert.Equal(MessageKind.Event, evt!.Kind);
            Assert.Equal("copy1", evt.Event!.ProfileId);
            Assert.Equal("COMPLETED", evt.Event.Code);

            // The file was copied to the target (the Job actually ran through the engine).
            Assert.True(File.Exists(Path.Combine(targetDir, "a.txt")));

            // State query reflects the processed Job (one closed, one profile, no in-flight remaining).
            IpcMessage stateResponse = await IpcTestEndpoints.RequestAsync(client, IpcMessage.ForStateQuery(), cts.Token);
            Assert.Equal(1, stateResponse.State!.ProfileCount);
            Assert.Equal(1L, stateResponse.State.ClosedCount);
        }
        finally
        {
            await host.StopAsync();
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }

    [Fact]
    public async Task CleanShutdown_DrainsPool_AndFlushesJournal()
    {
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        (string sourceDir, string targetDir) = WriteCopyProfile("copy2");

        const int fileCount = 12;
        for (int i = 0; i < fileCount; i++)
            File.WriteAllText(Path.Combine(sourceDir, $"f{i}.txt"), $"content-{i}");

        ServiceHost host = NewHost(endpoint, disableIpc: true);
        await host.StartAsync(cts.Token);

        // Submit the whole directory; many Jobs are now queued/in-flight.
        SubmitPayloadResult submit = host.Submit(new SubmitPayload(sourceDir, "copy2", true));
        Assert.True(submit.Accepted);
        Assert.Equal(fileCount, submit.QueuedCount);

        // Clean shutdown drains every accepted Job and flushes/disposes the durable files.
        await host.StopAsync(cts.Token);

        // Every file was copied (the pool drained, not abandoned).
        for (int i = 0; i < fileCount; i++)
            Assert.True(File.Exists(Path.Combine(targetDir, $"f{i}.txt")), $"f{i}.txt missing");

        // The journal was flushed and every Job CLOSED (no open entries remain) — proving the durable
        // journal was written and properly closed across the run + shutdown.
        var journal = FileJournal.FromConfig(new ServiceConfig(), _configDir);
        try
        {
            Assert.Empty(journal.ReadOpenEntries());
        }
        finally
        {
            journal.Dispose();
        }

        await host.DisposeAsync();
    }

    [Fact]
    public async Task Host_OpensNoNetworkListener()
    {
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        WriteCopyProfile("copy3");

        System.Net.IPEndPoint[] before =
            System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();

        await using ServiceHost host = NewHost(endpoint);
        try
        {
            await host.StartAsync(cts.Token);

            System.Net.IPEndPoint[] after =
                System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            System.Net.IPEndPoint[] added = after.Where(a => !before.Contains(a)).ToArray();
            Assert.Empty(added);
        }
        finally
        {
            await host.StopAsync();
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }

    [Fact]
    public async Task ReloadProfiles_PicksUpNewProfile()
    {
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        WriteCopyProfile("copyA");

        ServiceHost host = NewHost(endpoint, disableIpc: true);
        await host.StartAsync(cts.Token);
        try
        {
            Assert.Single(host.ListProfiles().Profiles);

            WriteCopyProfile("copyB");
            ReloadResult reload = host.ReloadProfiles();
            Assert.Equal(2, reload.LoadedCount);
            Assert.Empty(reload.Errors);
            Assert.Equal(2, host.ListProfiles().Profiles.Count);
        }
        finally
        {
            await host.StopAsync();
            IpcTestEndpoints.Cleanup(endpoint);
        }
    }
}
