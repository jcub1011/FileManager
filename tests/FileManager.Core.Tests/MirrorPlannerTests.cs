using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using FileManager.Core.Sync;
using FileManager.Core.Trash;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class MirrorPlannerTests : IDisposable
{
    private readonly TempDir _temp = new("mirror");
    private readonly SystemFileOperations _files = new();

    public void Dispose() => _temp.Dispose();

    private Profile MirrorProfile(IReadOnlyList<string> sources, IReadOnlyList<string> targets, TargetLayout layout) =>
        TestProfiles.Build(sources, targets, layout) with { SyncMode = SyncMode.Mirror };

    [Fact]
    public void Plan_IdentifiesSurplus_PresentAtTargetAbsentFromSource()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        _temp.WriteFile("S/keep.txt", "k");          // in source ⇒ not surplus
        _temp.WriteFile("T/keep.txt", "k");          // matches source key
        string surplus = _temp.WriteFile("T/orphan.txt", "o"); // absent from source ⇒ surplus

        var planner = new MirrorPlanner(_files, new LocalFolderTrash(_files, FakeFreeSpaceProbe.Unconstrained(), _temp.Path("trash")));
        MirrorPlan plan = planner.Plan(MirrorProfile(new[] { s }, new[] { t }, TargetLayout.PreserveStructure));

        Assert.Single(plan.Surplus);
        Assert.Equal(PathNormalizer.Normalize(surplus), PathNormalizer.Normalize(plan.Surplus[0].FilePath));
    }

    [Fact]
    public void Execute_RoutesSurplusToTrash_Recoverable_NeverHardDeletes()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        _temp.WriteFile("S/keep.txt", "k");
        _temp.WriteFile("T/keep.txt", "k");
        string surplus = _temp.WriteFile("T/surplus.txt", "save me");

        string trashRoot = _temp.Path("Trash");
        var planner = new MirrorPlanner(_files, new LinuxTrash(_files, FakeFreeSpaceProbe.Unconstrained(), trashRoot));

        MirrorExecution exec = planner.Reconcile(
            MirrorProfile(new[] { s }, new[] { t }, TargetLayout.PreserveStructure));

        Assert.True(exec.AllSucceeded);
        // Surplus removed from the Target...
        Assert.False(File.Exists(surplus));
        // ...but recoverable from the Trash (never hard-deleted).
        string recovered = Path.Combine(trashRoot, "files", "surplus.txt");
        Assert.True(File.Exists(recovered));
        Assert.Equal("save me", File.ReadAllText(recovered));
        // The kept file is untouched.
        Assert.True(File.Exists(Path.Combine(t, "keep.txt")));
    }

    [Fact]
    public void Plan_HonorsFlattenLayoutForAggregation()
    {
        // M:1 aggregation forces Flatten: the Target key is the bare file name.
        string s1 = _temp.MakeDir("S1");
        string s2 = _temp.MakeDir("S2");
        string t = _temp.MakeDir("T");
        _temp.WriteFile("S1/a.txt", "a");
        _temp.WriteFile("S2/b.txt", "b");
        _temp.WriteFile("T/a.txt", "a");           // matches S1 flattened
        _temp.WriteFile("T/b.txt", "b");           // matches S2 flattened
        string surplus = _temp.WriteFile("T/c.txt", "c"); // absent from both sources

        var planner = new MirrorPlanner(_files, new LocalFolderTrash(_files, FakeFreeSpaceProbe.Unconstrained(), _temp.Path("trash")));
        MirrorPlan plan = planner.Plan(MirrorProfile(new[] { s1, s2 }, new[] { t }, TargetLayout.PreserveStructure));

        Assert.Single(plan.Surplus);
        Assert.Equal("c.txt", Path.GetFileName(plan.Surplus[0].FilePath));
    }

    [Fact]
    public void Plan_IgnoresInFlightTempArtifacts()
    {
        string s = _temp.MakeDir("S");
        string t = _temp.MakeDir("T");
        _temp.WriteFile("S/keep.txt", "k");
        _temp.WriteFile("T/keep.txt", "k");
        // An orphaned atomic-write temp must not be reported as surplus.
        _temp.WriteFile("T/." + Guid.NewGuid().ToString("N") + AtomicFileWriter.TempSuffix, "junk");

        var planner = new MirrorPlanner(_files, new LocalFolderTrash(_files, FakeFreeSpaceProbe.Unconstrained(), _temp.Path("trash")));
        MirrorPlan plan = planner.Plan(MirrorProfile(new[] { s }, new[] { t }, TargetLayout.PreserveStructure));

        Assert.Empty(plan.Surplus);
    }
}
