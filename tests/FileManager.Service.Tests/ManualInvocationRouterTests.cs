using System.IO;
using FileManager.Contracts.Messages;
using FileManager.Core.Execution;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Service.Tests;

/// <summary>
/// Unit tests for the §3.2 always-prompt manual-invocation flow at the <see cref="ManualInvocationRouter"/>
/// level: a manual payload registers a pending invocation (nothing enqueued) with ALL owning profiles;
/// resolve-with-id enqueues the chosen profile's job(s); resolve-cancel discards; and a path matched by no
/// profile STILL yields a pending invocation with an empty Matches list (so the chooser appears with
/// "Create Profile…"). All against a real temp directory — deterministic, no IPC, no OS registration.
/// </summary>
public sealed class ManualInvocationRouterTests : IDisposable
{
    private readonly string _root;
    private readonly JobQueue _queue = new();
    private readonly PayloadQueue _payloads;
    private readonly List<Profile> _profiles = new();

    public ManualInvocationRouterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fm-manual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _payloads = new PayloadQueue(_queue, () => _profiles, new SystemFileOperations());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string MakeSource(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private Profile CopyProfile(string id, string sourceDir)
    {
        string targetDir = Path.Combine(_root, id + "-dst");
        Directory.CreateDirectory(targetDir);
        var profile = new Profile
        {
            SchemaVersion = 2,
            ProfileId = id,
            Name = "Profile " + id,
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
            Filters = new FilterSet(),
            Logging = new LoggingSpec { Verbosity = Verbosity.All, NotifyOnFailure = false },
        };
        _profiles.Add(profile);
        return profile;
    }

    private int DrainQueueCount()
    {
        int count = 0;
        while (_queue.Reader.TryRead(out _))
            count++;
        return count;
    }

    [Fact]
    public void Register_DoesNotEnqueue_AndListsAllOwningProfiles()
    {
        string src = MakeSource("shared");
        CopyProfile("p1", src);
        CopyProfile("p2", src); // both own the same source dir
        File.WriteAllText(Path.Combine(src, "a.txt"), "x");

        var router = new ManualInvocationRouter(_payloads);
        ManualInvocationPending pending = router.Register(new SubmitPayload(src, null, true, IsManual: true));

        // Nothing enqueued — the always-prompt invariant (no auto-run).
        Assert.Equal(0, DrainQueueCount());
        Assert.Equal(1, router.PendingCount);
        // ALL owning profiles surfaced, not just the best match.
        Assert.Equal(2, pending.Matches.Count);
        Assert.Contains(pending.Matches, m => m.ProfileId == "p1");
        Assert.Contains(pending.Matches, m => m.ProfileId == "p2");
    }

    [Fact]
    public void Resolve_WithChosenProfile_EnqueuesThatProfilesJobs()
    {
        string src = MakeSource("chosen");
        CopyProfile("p1", src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "x");
        File.WriteAllText(Path.Combine(src, "b.txt"), "y");

        var router = new ManualInvocationRouter(_payloads);
        ManualInvocationPending pending = router.Register(new SubmitPayload(src, null, true, IsManual: true));
        Assert.Equal(0, DrainQueueCount());

        SubmitPayloadResult result = router.Resolve(new ResolveManualInvocation(pending.InvocationId, "p1"));

        Assert.True(result.Accepted);
        Assert.Equal(2, result.QueuedCount);
        Assert.Equal(2, DrainQueueCount());
        Assert.Equal(0, router.PendingCount); // one-shot
    }

    [Fact]
    public void Resolve_Cancel_DiscardsWithoutEnqueue()
    {
        string src = MakeSource("cancelme");
        CopyProfile("p1", src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "x");

        var router = new ManualInvocationRouter(_payloads);
        ManualInvocationPending pending = router.Register(new SubmitPayload(src, null, true, IsManual: true));

        SubmitPayloadResult result = router.Resolve(new ResolveManualInvocation(pending.InvocationId, null));

        Assert.False(result.Accepted);
        Assert.Equal(0, DrainQueueCount());
        Assert.Equal(0, router.PendingCount);
    }

    [Fact]
    public void Register_PathWithNoMatchingProfile_StillPendsWithEmptyMatches()
    {
        // A path under no configured Source — the chooser must still appear (with Create Profile…).
        string orphan = MakeSource("orphan");
        File.WriteAllText(Path.Combine(orphan, "a.txt"), "x");

        var router = new ManualInvocationRouter(_payloads);
        ManualInvocationPending pending = router.Register(new SubmitPayload(orphan, null, true, IsManual: true));

        Assert.Empty(pending.Matches);
        Assert.Equal(1, router.PendingCount);
        Assert.Equal(0, DrainQueueCount());
    }

    [Fact]
    public void Resolve_UnknownId_IsRejected()
    {
        var router = new ManualInvocationRouter(_payloads);
        SubmitPayloadResult result = router.Resolve(new ResolveManualInvocation("does-not-exist", "p1"));
        Assert.False(result.Accepted);
    }

    [Fact]
    public void Register_ExpiresStalePendings()
    {
        string src = MakeSource("expiry");
        CopyProfile("p1", src);
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var router = new ManualInvocationRouter(_payloads, clock, timeToLive: TimeSpan.FromMinutes(5));

        router.Register(new SubmitPayload(src, null, true, IsManual: true));
        Assert.Equal(1, router.PendingCount);

        clock.Advance(TimeSpan.FromMinutes(10)); // past the TTL
        router.Register(new SubmitPayload(src, null, true, IsManual: true)); // triggers ExpireStale

        Assert.Equal(1, router.PendingCount); // the stale one expired; only the new one remains
    }
}
