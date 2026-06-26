using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using FileManager.Core.Routing;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class ConflictResolverTests : IDisposable
{
    private readonly TempDir _temp = new("conflict");
    private readonly SystemFileOperations _files = new();
    private readonly ConflictResolver _resolver;

    public ConflictResolverTests() => _resolver = new ConflictResolver(_files);

    public void Dispose() => _temp.Dispose();

    private static FileMetadata MetaWithModified(DateTime modifiedUtc) => new()
    {
        Length = 10,
        LastWriteTimeUtc = modifiedUtc,
        CreationTimeUtc = modifiedUtc,
        IsHidden = false,
        IsSystem = false,
        IsSymlink = false,
    };

    [Fact]
    public void NoExistingFile_IsPlainWrite()
    {
        string dest = _temp.Path("out", "a.txt");
        ConflictOutcome o = _resolver.Resolve(
            dest, MetaWithModified(DateTime.UtcNow), ConflictResolution.Overwrite);

        Assert.Equal(TargetAction.Written, o.Action);
        Assert.Equal(dest, o.FinalPath);
        Assert.False(o.Overwrite);
    }

    [Fact]
    public void Overwrite_ReplacesExisting()
    {
        string dest = _temp.WriteFile("out/a.txt", "old");
        ConflictOutcome o = _resolver.Resolve(
            dest, MetaWithModified(DateTime.UtcNow), ConflictResolution.Overwrite);

        Assert.Equal(TargetAction.Overwritten, o.Action);
        Assert.True(o.Overwrite);
        Assert.Equal(dest, o.FinalPath);
    }

    [Fact]
    public void OverwriteIfNewer_IncomingNewer_Overwrites()
    {
        string dest = _temp.WriteFile("out/a.txt", "old");
        File.SetLastWriteTimeUtc(dest, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        ConflictOutcome o = _resolver.Resolve(
            dest, MetaWithModified(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ConflictResolution.OverwriteIfNewer);

        Assert.Equal(TargetAction.Overwritten, o.Action);
        Assert.True(o.Overwrite);
    }

    [Fact]
    public void OverwriteIfNewer_IncomingOlder_Skips()
    {
        string dest = _temp.WriteFile("out/a.txt", "new");
        File.SetLastWriteTimeUtc(dest, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        ConflictOutcome o = _resolver.Resolve(
            dest, MetaWithModified(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ConflictResolution.OverwriteIfNewer);

        Assert.Equal(TargetAction.Skipped, o.Action);
    }

    [Fact]
    public void RenameSuffix_PicksFirstFreeIncrement()
    {
        string dest = _temp.WriteFile("out/a.txt", "existing");

        ConflictOutcome o = _resolver.Resolve(
            dest, MetaWithModified(DateTime.UtcNow), ConflictResolution.RenameSuffix);

        Assert.Equal(TargetAction.RenamedSuffix, o.Action);
        Assert.Equal(_temp.Path("out", "a (1).txt"), o.FinalPath);
        Assert.False(o.Overwrite);
    }

    [Fact]
    public void RenameSuffix_SkipsTakenSuffixes()
    {
        string dest = _temp.WriteFile("out/a.txt", "existing");
        _temp.WriteFile("out/a (1).txt", "taken");

        ConflictOutcome o = _resolver.Resolve(
            dest, MetaWithModified(DateTime.UtcNow), ConflictResolution.RenameSuffix);

        Assert.Equal(_temp.Path("out", "a (2).txt"), o.FinalPath);
    }

    [Fact]
    public void Skip_LeavesExisting()
    {
        string dest = _temp.WriteFile("out/a.txt", "existing");

        ConflictOutcome o = _resolver.Resolve(
            dest, MetaWithModified(DateTime.UtcNow), ConflictResolution.Skip);

        Assert.Equal(TargetAction.Skipped, o.Action);
        Assert.Null(o.FinalPath);
    }
}
