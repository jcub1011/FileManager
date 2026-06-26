using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Jobs;
using FileManager.Core.Logging;
using FileManager.Core.Profiles;
using FileManager.Core.Routing;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class JobEngineTests : IDisposable
{
    private static readonly IngestionContext Ctx =
        new() { Now = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero) };

    private readonly TempDir _temp = new("engine");
    private readonly InMemoryLogSink _log = new();
    private readonly JobEngine _engine;

    public JobEngineTests()
    {
        _engine = new JobEngine(
            new SystemFileOperations(), _log, new JobEngineOptions { TrashDirectory = _temp.Path("trash") });
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void OneToOne_PreserveStructure_PlacesNestedFile()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/sub/a.txt", "content");
        Profile p = TestProfiles.Build(new[] { s }, new[] { t }, TargetLayout.PreserveStructure);

        JobResult r = _engine.ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Closed, r.State);
        string dest = Path.Combine(t, "sub", "a.txt");
        Assert.True(File.Exists(dest));
        Assert.Equal("content", File.ReadAllText(dest));
    }

    [Fact]
    public void OneToMany_WritesToAllTargets()
    {
        string s = _temp.MakeDir("S");
        string t1 = _temp.MakeDir("T1");
        string t2 = _temp.MakeDir("T2");
        string file = _temp.WriteFile("S/a.txt", "dup");
        Profile p = TestProfiles.Build(new[] { s }, new[] { t1, t2 });

        JobResult r = _engine.ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Closed, r.State);
        Assert.True(File.Exists(Path.Combine(t1, "a.txt")));
        Assert.True(File.Exists(Path.Combine(t2, "a.txt")));
        Assert.Equal(2, r.Targets.Count);
    }

    [Fact]
    public void ManyToOne_FlattensIntoSingleTarget()
    {
        string s1 = _temp.MakeDir("S1");
        string s2 = _temp.MakeDir("S2");
        string t = _temp.MakeDir("T");
        string f1 = _temp.WriteFile("S1/x/a.txt", "a");
        string f2 = _temp.WriteFile("S2/y/b.txt", "b");
        // PreserveStructure configured, but M:1 forces Flatten.
        Profile p = TestProfiles.Build(new[] { s1, s2 }, new[] { t }, TargetLayout.PreserveStructure);

        _engine.ProcessFile(p, f1, Ctx);
        _engine.ProcessFile(p, f2, Ctx);

        Assert.True(File.Exists(Path.Combine(t, "a.txt")));
        Assert.True(File.Exists(Path.Combine(t, "b.txt")));
        Assert.False(Directory.Exists(Path.Combine(t, "x")));
        Assert.False(Directory.Exists(Path.Combine(t, "y")));
    }

    [Fact]
    public void ManyToMany_PreservesStructureAtEachTarget()
    {
        string s1 = _temp.MakeDir("S1");
        string s2 = _temp.MakeDir("S2");
        string t1 = _temp.MakeDir("T1");
        string t2 = _temp.MakeDir("T2");
        string f1 = _temp.WriteFile("S1/x/a.txt", "a");
        Profile p = TestProfiles.Build(new[] { s1, s2 }, new[] { t1, t2 }, TargetLayout.PreserveStructure);

        _engine.ProcessFile(p, f1, Ctx);

        Assert.True(File.Exists(Path.Combine(t1, "x", "a.txt")));
        Assert.True(File.Exists(Path.Combine(t2, "x", "a.txt")));
    }

    [Fact]
    public void ManyToOne_SourceOrderPriority_FirstSourceWinsUnderSkip()
    {
        string s1 = _temp.MakeDir("S1");
        string s2 = _temp.MakeDir("S2");
        string t = _temp.MakeDir("T");
        string f1 = _temp.WriteFile("S1/a.txt", "from-s1");
        string f2 = _temp.WriteFile("S2/a.txt", "from-s2");
        PolicySet skip = TestProfiles.DefaultPolicies with { ConflictResolution = ConflictResolution.Skip };
        Profile p = TestProfiles.Build(new[] { s1, s2 }, new[] { t }, policies: skip);

        // Process in Profile source order: S1 (higher priority) first.
        _engine.ProcessFile(p, f1, Ctx);
        JobResult second = _engine.ProcessFile(p, f2, Ctx);

        string dest = Path.Combine(t, "a.txt");
        Assert.Equal("from-s1", File.ReadAllText(dest));
        Assert.Equal(TargetAction.Skipped, second.Targets[0].Action);
    }

    [Fact]
    public void RenameSuffix_EndToEnd_WritesBesideExisting()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        File.WriteAllText(Path.Combine(t, "a.txt"), "existing");
        string file = _temp.WriteFile("S/a.txt", "incoming");
        PolicySet rename = TestProfiles.DefaultPolicies with { ConflictResolution = ConflictResolution.RenameSuffix };
        Profile p = TestProfiles.Build(new[] { s }, new[] { t }, policies: rename);

        JobResult r = _engine.ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Closed, r.State);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(t, "a.txt")));
        Assert.Equal("incoming", File.ReadAllText(Path.Combine(t, "a (1).txt")));
        Assert.Equal(TargetAction.RenamedSuffix, r.Targets[0].Action);
    }

    [Fact]
    public void FilteredOut_LogsSkippedWithDecidingFilter()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/notes.txt", "x");
        var filters = new FilterSet { Include = new[] { "*.wav" } };
        Profile p = TestProfiles.Build(new[] { s }, new[] { t }, filters: filters);

        JobResult r = _engine.ProcessFile(p, file, Ctx);

        Assert.Equal(JobState.Skipped, r.State);
        Assert.Equal("Include", r.DecidingFilter);
        Assert.False(File.Exists(Path.Combine(t, "notes.txt")));
        Assert.Contains(_log.Entries, e => e.Code == "SKIPPED" && e.Message.Contains("Include"));
    }

    [Fact]
    public void KeepSource_LeavesSourceUnchanged()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string file = _temp.WriteFile("S/a.txt", "keep");
        Profile p = TestProfiles.Build(new[] { s }, new[] { t }); // default OnSuccess = KeepSource

        JobResult r = _engine.ProcessFile(p, file, Ctx);

        Assert.Equal(OnSuccess.KeepSource, r.Disposition);
        Assert.True(File.Exists(file));
        Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact]
    public void UnknownSource_FailsGracefully()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        string outside = _temp.WriteFile("elsewhere/a.txt", "x");
        Profile p = TestProfiles.Build(new[] { s }, new[] { t });

        JobResult r = _engine.ProcessFile(p, outside, Ctx);

        Assert.Equal(JobState.Failed, r.State);
        Assert.NotNull(r.FailureReason);
    }
}
