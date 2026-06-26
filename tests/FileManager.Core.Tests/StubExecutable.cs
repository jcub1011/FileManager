using System.IO;

namespace FileManager.Core.Tests;

/// <summary>
/// Resolves the built <c>FileManager.TestStub</c> apphost, which the test project's ProjectReference
/// copies next to the test assembly. Tests use this as a real transformer executable.
/// </summary>
internal static class StubExecutable
{
    /// <summary>Absolute path of the native stub executable for this platform.</summary>
    public static string Path { get; } = Resolve();

    private static string Resolve()
    {
        string name = "FileManager.TestStub" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Test stub executable not found at '{path}'.", path);
        return path;
    }
}
