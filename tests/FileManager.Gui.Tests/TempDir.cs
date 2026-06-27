using System.IO;

namespace FileManager.Gui.Tests;

/// <summary>A unique temp directory that cleans itself up on dispose (mirrors the Core test helper).</summary>
internal sealed class TempDir : IDisposable
{
    public string Root { get; }

    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "fmgui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
