using System.IO;
using FileManager.Core.Profiles;
using FileManager.Core.Routing;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class TargetResolverTests
{
    [Fact]
    public void ResolveLayout_OneToOne_HonorsPreserve()
    {
        Profile p = TestProfiles.Build(new[] { "S" }, new[] { "T" }, TargetLayout.PreserveStructure);
        Assert.Equal(TargetLayout.PreserveStructure, TargetResolver.ResolveLayout(p));
    }

    [Fact]
    public void ResolveLayout_OneToMany_HonorsPreserve()
    {
        Profile p = TestProfiles.Build(new[] { "S" }, new[] { "T1", "T2" }, TargetLayout.PreserveStructure);
        Assert.Equal(TargetLayout.PreserveStructure, TargetResolver.ResolveLayout(p));
    }

    [Fact]
    public void ResolveLayout_ManyToOne_ForcesFlatten()
    {
        Profile p = TestProfiles.Build(new[] { "S1", "S2" }, new[] { "T" }, TargetLayout.PreserveStructure);
        Assert.Equal(TargetLayout.Flatten, TargetResolver.ResolveLayout(p));
    }

    [Fact]
    public void ResolveLayout_ManyToMany_HonorsConfiguredLayout()
    {
        Profile p = TestProfiles.Build(new[] { "S1", "S2" }, new[] { "T1", "T2" }, TargetLayout.PreserveStructure);
        Assert.Equal(TargetLayout.PreserveStructure, TargetResolver.ResolveLayout(p));
    }

    [Fact]
    public void ResolveDestination_PreserveStructure_RecreatesRelativePath()
    {
        var target = new TargetSpec { Path = Path.Combine("root", "out") };
        string dest = TargetResolver.ResolveDestination(
            target, Path.Combine("sub", "a.txt"), "a.txt", TargetLayout.PreserveStructure);
        Assert.Equal(Path.Combine("root", "out", "sub", "a.txt"), dest);
    }

    [Fact]
    public void ResolveDestination_Flatten_DropsIntoRoot()
    {
        var target = new TargetSpec { Path = Path.Combine("root", "out") };
        string dest = TargetResolver.ResolveDestination(
            target, Path.Combine("sub", "a.txt"), "a.txt", TargetLayout.Flatten);
        Assert.Equal(Path.Combine("root", "out", "a.txt"), dest);
    }
}
