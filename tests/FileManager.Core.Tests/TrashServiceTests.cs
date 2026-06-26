using System.IO;
using FileManager.Core.IO;
using FileManager.Core.Trash;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class TrashServiceTests : IDisposable
{
    private readonly TempDir _temp = new("trash");
    private readonly SystemFileOperations _files = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void LinuxTrash_MovesFileToFilesDir_AndWritesTrashInfo()
    {
        string trashRoot = _temp.Path("Trash");
        string src = _temp.WriteFile("docs/report.txt", "recoverable content");
        var trash = new LinuxTrash(_files, FakeFreeSpaceProbe.Unconstrained(), trashRoot);

        TrashResult r = trash.MoveToTrash(src);

        Assert.True(r.Ok);
        Assert.False(File.Exists(src));

        string movedFile = Path.Combine(trashRoot, "files", "report.txt");
        Assert.True(File.Exists(movedFile));
        Assert.Equal("recoverable content", File.ReadAllText(movedFile));

        string infoFile = Path.Combine(trashRoot, "info", "report.txt.trashinfo");
        Assert.True(File.Exists(infoFile));
        string info = File.ReadAllText(infoFile);
        Assert.Contains("[Trash Info]", info);
        Assert.Contains("Path=", info);
        Assert.Contains("DeletionDate=", info);
        // The original absolute path is recorded URL-encoded (separators preserved).
        Assert.Contains(LinuxTrash.EncodePath(Path.GetFullPath(src)), info);
    }

    [Fact]
    public void LinuxTrash_HandlesNameCollisionsAcrossFilesAndInfo()
    {
        string trashRoot = _temp.Path("Trash");
        var trash = new LinuxTrash(_files, FakeFreeSpaceProbe.Unconstrained(), trashRoot);

        string first = _temp.WriteFile("a/note.txt", "first");
        string second = _temp.WriteFile("b/note.txt", "second");

        Assert.True(trash.MoveToTrash(first).Ok);
        Assert.True(trash.MoveToTrash(second).Ok);

        // Both recoverable: the base name and a (1) variant exist in files/ with matching .trashinfo.
        Assert.True(File.Exists(Path.Combine(trashRoot, "files", "note.txt")));
        Assert.True(File.Exists(Path.Combine(trashRoot, "files", "note (1).txt")));
        Assert.True(File.Exists(Path.Combine(trashRoot, "info", "note.txt.trashinfo")));
        Assert.True(File.Exists(Path.Combine(trashRoot, "info", "note (1).txt.trashinfo")));
    }

    [Fact]
    public void EncodePath_PercentEncodesComponentsButKeepsSeparators()
    {
        string encoded = LinuxTrash.EncodePath("/home/user/My Docs/a b.txt");

        Assert.StartsWith("/home/user/", encoded);
        Assert.Contains("My%20Docs", encoded);
        Assert.Contains("a%20b.txt", encoded);
    }

    [Fact]
    public void LocalFolderTrash_MovesFileIntoFolder_Recoverable()
    {
        string trashRoot = _temp.Path("local-trash");
        string src = _temp.WriteFile("x/data.bin", "bytes");
        var trash = new LocalFolderTrash(_files, FakeFreeSpaceProbe.Unconstrained(), trashRoot);

        TrashResult r = trash.MoveToTrash(src);

        Assert.True(r.Ok);
        Assert.False(File.Exists(src));
        Assert.NotNull(r.TrashedPath);
        Assert.True(File.Exists(r.TrashedPath!));
        Assert.Equal("bytes", File.ReadAllText(r.TrashedPath!));
    }

    [Fact]
    public void LinuxTrash_FullTrashVolume_Fails_FileUntouched()
    {
        string trashRoot = _temp.Path("Trash");
        string src = _temp.WriteFile("docs/report.txt", "recoverable content");
        // The trash volume reports 0 free → the proactive check refuses before any move.
        var probe = new FakeFreeSpaceProbe(new Dictionary<string, long> { [trashRoot] = 0 });
        var trash = new LinuxTrash(_files, probe, trashRoot);

        TrashResult r = trash.MoveToTrash(src);

        Assert.False(r.Ok);
        Assert.Contains("insufficient space", r.Reason);
        // The real file is untouched.
        Assert.True(File.Exists(src));
        Assert.Equal("recoverable content", File.ReadAllText(src));
    }

    [Fact]
    public void LocalFolderTrash_FullTrashVolume_Fails_FileUntouched()
    {
        string trashRoot = _temp.Path("local-trash");
        string src = _temp.WriteFile("x/data.bin", "bytes");
        var probe = new FakeFreeSpaceProbe(new Dictionary<string, long> { [trashRoot] = 0 });
        var trash = new LocalFolderTrash(_files, probe, trashRoot);

        TrashResult r = trash.MoveToTrash(src);

        Assert.False(r.Ok);
        Assert.Contains("insufficient space", r.Reason);
        Assert.True(File.Exists(src));
        Assert.Equal("bytes", File.ReadAllText(src));
    }
}
