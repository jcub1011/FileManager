using System.IO;
using FileManager.Core.Disposition;
using FileManager.Core.IO;
using FileManager.Core.Profiles;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class SourceDisposerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 9, 30, 0, TimeSpan.Zero);
    private readonly TempDir _temp = new("dispose");
    private readonly SystemFileOperations _files = new();
    private readonly SourceDisposer _disposer;

    public SourceDisposerTests() => _disposer = new SourceDisposer(_files);

    public void Dispose() => _temp.Dispose();

    private PolicySet Policies(OnSuccess onSuccess, string? archive = null) =>
        TestProfiles.DefaultPolicies with { OnSuccess = onSuccess, ArchiveFolder = archive };

    [Fact]
    public void KeepSource_LeavesFileInPlace()
    {
        string src = _temp.WriteFile("src/a.txt", "keep");

        DispositionOutcome o = _disposer.Dispose(
            src, Policies(OnSuccess.KeepSource), _temp.Path("trash"), Now);

        Assert.Equal(OnSuccess.KeepSource, o.Action);
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void PermanentDelete_RemovesFile()
    {
        string src = _temp.WriteFile("src/a.txt", "gone");

        DispositionOutcome o = _disposer.Dispose(
            src, Policies(OnSuccess.PermanentDelete), _temp.Path("trash"), Now);

        Assert.Equal(OnSuccess.PermanentDelete, o.Action);
        Assert.False(File.Exists(src));
    }

    [Fact]
    public void MoveToArchive_MovesIntoArchiveFolder()
    {
        string src = _temp.WriteFile("src/a.txt", "archive-me");
        string archive = _temp.Path("archive");

        DispositionOutcome o = _disposer.Dispose(
            src, Policies(OnSuccess.MoveToArchive, archive), _temp.Path("trash"), Now);

        Assert.Equal(OnSuccess.MoveToArchive, o.Action);
        Assert.False(File.Exists(src));
        Assert.NotNull(o.ResultPath);
        Assert.True(File.Exists(o.ResultPath!));
        Assert.Equal(archive, Path.GetDirectoryName(o.ResultPath));
        Assert.Equal("archive-me", File.ReadAllText(o.ResultPath!));
    }

    [Fact]
    public void MoveToTrash_PlaceholderMovesIntoTrashWithTimestamp()
    {
        string src = _temp.WriteFile("src/a.txt", "trash-me");
        string trash = _temp.Path("trash");

        DispositionOutcome o = _disposer.Dispose(
            src, Policies(OnSuccess.MoveToTrash), trash, Now);

        Assert.Equal(OnSuccess.MoveToTrash, o.Action);
        Assert.False(File.Exists(src));
        Assert.NotNull(o.ResultPath);
        Assert.True(File.Exists(o.ResultPath!));
        Assert.Equal(trash, Path.GetDirectoryName(o.ResultPath));
        Assert.StartsWith("20260625-093000-", Path.GetFileName(o.ResultPath));
    }
}
