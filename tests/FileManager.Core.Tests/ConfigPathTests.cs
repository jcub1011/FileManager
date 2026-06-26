using System.IO;
using System.Runtime.InteropServices;
using FileManager.Core.Configuration;
using Xunit;

namespace FileManager.Core.Tests;

public sealed class ConfigPathTests
{
    [Fact]
    public void Windows_UsesAppDataFileManager()
    {
        string dir = ConfigPaths.BuildConfigDirectory(
            isWindows: true,
            appData: @"C:\Users\bob\AppData\Roaming",
            xdgConfigHome: null,
            home: @"C:\Users\bob");

        Assert.Equal(Path.Combine(@"C:\Users\bob\AppData\Roaming", "FileManager"), dir);
    }

    [Fact]
    public void Windows_FallsBackToHomeWhenAppDataMissing()
    {
        string dir = ConfigPaths.BuildConfigDirectory(
            isWindows: true,
            appData: null,
            xdgConfigHome: null,
            home: @"C:\Users\bob");

        Assert.Equal(Path.Combine(@"C:\Users\bob", "AppData", "Roaming", "FileManager"), dir);
    }

    [Fact]
    public void Linux_PrefersXdgConfigHome()
    {
        string dir = ConfigPaths.BuildConfigDirectory(
            isWindows: false,
            appData: null,
            xdgConfigHome: "/home/bob/.config",
            home: "/home/bob");

        Assert.Equal(Path.Combine("/home/bob/.config", "filemanager"), dir);
    }

    [Fact]
    public void Linux_FallsBackToDotConfigWhenXdgUnset()
    {
        string dir = ConfigPaths.BuildConfigDirectory(
            isWindows: false,
            appData: null,
            xdgConfigHome: null,
            home: "/home/bob");

        Assert.Equal(Path.Combine("/home/bob", ".config", "filemanager"), dir);
    }

    [Fact]
    public void CurrentOS_ResolvesExpectedSubpaths()
    {
        string configDir = ConfigPaths.GetConfigDirectory();

        Assert.Equal(Path.Combine(configDir, "profiles"), ConfigPaths.GetProfilesDirectory());
        Assert.Equal(Path.Combine(configDir, "config.json"), ConfigPaths.GetConfigFilePath());

        string expectedFolderName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "FileManager"
            : "filemanager";
        Assert.EndsWith(expectedFolderName, configDir);
    }
}
