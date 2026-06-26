using System.IO;
using FileManager.Core.IO;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class PathNormalizerTests
{
    [Fact]
    public void Normalize_StripsTrailingSeparator()
    {
        string p = Path.Combine(Path.GetTempPath(), "fp-norm");
        string withSep = p + Path.DirectorySeparatorChar;
        Assert.Equal(PathNormalizer.Normalize(p), PathNormalizer.Normalize(withSep));
    }

    [Fact]
    public void IsUnder_NestedPath_True()
    {
        string root = Path.Combine(Path.GetTempPath(), "fp-root");
        string nested = Path.Combine(root, "sub", "file.txt");
        Assert.True(PathNormalizer.IsUnder(root, nested));
    }

    [Fact]
    public void IsUnder_EqualPath_True()
    {
        string root = Path.Combine(Path.GetTempPath(), "fp-root");
        Assert.True(PathNormalizer.IsUnder(root, root));
    }

    [Fact]
    public void IsUnder_SiblingPrefix_False()
    {
        string root = Path.Combine(Path.GetTempPath(), "fp-root");
        string sibling = Path.Combine(Path.GetTempPath(), "fp-root-other", "file.txt");
        Assert.False(PathNormalizer.IsUnder(root, sibling));
    }

    [Fact]
    public void GetRelativePath_ReturnsNestedPortion()
    {
        string root = Path.Combine(Path.GetTempPath(), "fp-root");
        string nested = Path.Combine(root, "a", "b.txt");
        Assert.Equal(Path.Combine("a", "b.txt"), PathNormalizer.GetRelativePath(root, nested));
    }

    [Fact]
    public void AreEqual_CaseInsensitiveOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(PathNormalizer.AreEqual(@"C:\Temp\File.txt", @"c:\temp\file.txt"));
    }
}
