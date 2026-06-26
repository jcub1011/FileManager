using System.IO;
using FileManager.Core.IO;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class AtomicFileWriterTests : IDisposable
{
    private readonly TempDir _temp = new("atomic");
    private readonly SystemFileOperations _files = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Write_CreatesDestinationAndDirs_NoTempLeftover()
    {
        string src = _temp.WriteFile("src/a.txt", "hello");
        string dest = _temp.Path("out", "nested", "a.txt");

        AtomicFileWriter.Write(_files, src, dest, overwrite: false);

        Assert.True(File.Exists(dest));
        Assert.Equal("hello", File.ReadAllText(dest));
        Assert.Empty(Directory.GetFiles(_temp.Path("out", "nested"), ".*.fmtmp"));
    }

    [Fact]
    public void Write_Overwrite_ReplacesContent()
    {
        string src = _temp.WriteFile("src/a.txt", "new-content");
        string dest = _temp.WriteFile("out/a.txt", "old-content");

        AtomicFileWriter.Write(_files, src, dest, overwrite: true);

        Assert.Equal("new-content", File.ReadAllText(dest));
    }

    [Fact]
    public void Write_LargeFile_StreamsContentCorrectly()
    {
        // 8 MiB exceeds the 1 MiB copy buffer many times over, exercising the streamed loop.
        byte[] payload = new byte[8 * 1024 * 1024];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);

        string src = _temp.Path("src", "big.bin");
        Directory.CreateDirectory(_temp.Path("src"));
        File.WriteAllBytes(src, payload);

        string dest = _temp.Path("out", "big.bin");
        AtomicFileWriter.Write(_files, src, dest, overwrite: false);

        Assert.True(File.ReadAllBytes(dest).AsSpan().SequenceEqual(payload));
        Assert.Empty(Directory.GetFiles(_temp.Path("out"), ".*.fmtmp"));
    }
}
