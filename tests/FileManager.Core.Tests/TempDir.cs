using System.IO;

namespace FileManager.Core.Tests;

/// <summary>A unique temp directory that cleans itself up on dispose; helpers for seeding files.</summary>
internal sealed class TempDir : IDisposable
{
    public string Root { get; }

    public TempDir(string purpose)
    {
        Root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"fp-{purpose}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Absolute path under the temp root (does not create anything).</summary>
    public string Path(params string[] segments) =>
        System.IO.Path.Combine(new[] { Root }.Concat(segments).ToArray());

    /// <summary>Creates a file (and parent dirs) with <paramref name="content"/>; returns its path.</summary>
    public string WriteFile(string relativePath, string content = "data")
    {
        string full = Path(relativePath.Split('/', '\\'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Creates a directory (and parents) under the root; returns its path.</summary>
    public string MakeDir(string relativePath)
    {
        string full = Path(relativePath.Split('/', '\\'));
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
