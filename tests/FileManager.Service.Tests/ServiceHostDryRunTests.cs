using System.IO;
using System.Security.Cryptography;
using FileManager.Contracts.Messages;
using FileManager.Core.Configuration;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Service.Tests;

/// <summary>
/// End-to-end dry-run tests over a real <see cref="ServiceHost"/> + IPC: a dry-run request over a
/// populated sandbox returns a populated, structured report (matches, commands, Target writes,
/// disposition) AND makes zero filesystem changes — the §8 / §12 guarantee enforced at the wire level.
/// </summary>
public sealed class ServiceHostDryRunTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);
    private readonly string _configDir;

    public ServiceHostDryRunTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "fm-dryrun-" + Guid.NewGuid().ToString("N"));
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

    private (string SourceDir, string TargetDir) WriteProfile(string profileId, OnSuccess onSuccess)
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
                VerificationMethod = VerificationMethod.SHA256,
                OnSuccess = onSuccess,
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

    private static IReadOnlyDictionary<string, string> HashTree(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using var sha = SHA256.Create();
            map[Path.GetRelativePath(root, file)] = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(file)));
        }

        return map;
    }

    [Fact]
    public async Task DryRun_OverPopulatedSandbox_ReturnsPopulatedReport_AndChangesNothing()
    {
        using var cts = new CancellationTokenSource(Generous);
        string endpoint = IpcTestEndpoints.UniqueEndpoint();
        (string sourceDir, string targetDir) = WriteProfile("dr1", OnSuccess.PermanentDelete);

        File.WriteAllText(Path.Combine(sourceDir, "a.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(sourceDir, "sub"));
        File.WriteAllText(Path.Combine(sourceDir, "sub", "b.txt"), "bravo");
        File.WriteAllText(Path.Combine(targetDir, "a.txt"), "existing"); // forces an Overwritten action

        // Snapshot the source + target trees (the host writes its own log/journal under the config dir,
        // so we assert the no-mutation guarantee on the data the dry-run reasons about).
        string profileRoot = Path.Combine(_configDir, "dr1");
        IReadOnlyDictionary<string, string> before = HashTree(profileRoot);

        await using ServiceHost host = NewHost(endpoint);
        try
        {
            await host.StartAsync(cts.Token);
            await using Stream client = await IpcTestEndpoints.ConnectAsync(endpoint, Generous, cts.Token);

            IpcMessage response = await IpcTestEndpoints.RequestAsync(
                client, IpcMessage.ForDryRun(new DryRunRequest(sourceDir, "dr1", Recursive: true)), cts.Token);

            Assert.Equal(MessageKind.DryRunReport, response.Kind);
            DryRunReport report = response.DryRunReport!;
            Assert.True(report.Implemented);
            Assert.Equal("dr1", report.ProfileId);

            // Both source files matched; each plans a Target write and a (PermanentDelete) disposition.
            Assert.Equal(2, report.Matches.Count);
            Assert.Equal(2, report.TargetWrites.Count);
            Assert.Equal(2, report.Dispositions.Count);
            Assert.All(report.Dispositions, d => Assert.Equal(nameof(OnSuccess.PermanentDelete), d.Action));
            Assert.Contains(report.TargetWrites, w => w.Action == "Overwritten");
            Assert.Contains(report.TargetWrites, w => w.Action == "Written");
        }
        finally
        {
            await host.StopAsync();
            IpcTestEndpoints.Cleanup(endpoint);
        }

        // Zero filesystem changes to the source + target trees the dry-run reasoned about.
        IReadOnlyDictionary<string, string> after = HashTree(profileRoot);
        Assert.Equal(before.Count, after.Count);
        Assert.All(before, kv => Assert.Equal(kv.Value, after[kv.Key]));
        // The PermanentDelete disposition must NOT have removed the source.
        Assert.True(File.Exists(Path.Combine(sourceDir, "a.txt")));
        Assert.True(File.Exists(Path.Combine(sourceDir, "sub", "b.txt")));
    }
}
