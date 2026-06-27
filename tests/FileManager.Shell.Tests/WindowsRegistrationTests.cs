using FileManager.Shell.Windows;
using Xunit;

namespace FileManager.Shell.Tests;

/// <summary>
/// Asserts the PURE Windows shell-registration generators — the HKCU verb entries for the three node
/// types and the <c>IExplorerCommand</c> verb logic. No real registry write, no COM activation: these run
/// identically on Windows and Linux CI.
/// </summary>
public sealed class WindowsRegistrationTests
{
    private const string Launcher = @"C:\Program Files\FileManager\FileManager.Shell.exe";

    [Fact]
    public void RegistryVerbs_GeneratesEntriesForThreeNodeTypes()
    {
        IReadOnlyList<RegistryVerbEntry> entries = RegistryVerbs.BuildEntries(Launcher);

        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e =>
            e.KeyPath == @"Software\Classes\Directory\shell\FileManagerRun\command");
        Assert.Contains(entries, e =>
            e.KeyPath == @"Software\Classes\Directory\Background\shell\FileManagerRun\command");
        Assert.Contains(entries, e =>
            e.KeyPath == @"Software\Classes\AllFilesystemObjects\shell\FileManagerRun\command");
    }

    [Fact]
    public void RegistryVerbs_CommandsInvokeLauncherWithManualFlag()
    {
        IReadOnlyList<RegistryVerbEntry> entries = RegistryVerbs.BuildEntries(Launcher);

        foreach (RegistryVerbEntry entry in entries)
        {
            Assert.Contains(Launcher, entry.Command);
            Assert.Contains("--manual", entry.Command);
        }
    }

    [Fact]
    public void RegistryVerbs_BackgroundUsesFocusedFolderToken_OthersUseItemToken()
    {
        IReadOnlyList<RegistryVerbEntry> entries = RegistryVerbs.BuildEntries(Launcher);

        RegistryVerbEntry background = entries.Single(e => e.KeyPath.Contains(@"Directory\Background"));
        Assert.Contains("%V", background.Command);

        RegistryVerbEntry directory = entries.Single(e =>
            e.KeyPath == @"Software\Classes\Directory\shell\FileManagerRun\command");
        Assert.Contains("%1", directory.Command);
    }

    [Fact]
    public void RegistryVerbs_VerbKeyPaths_StripCommandSuffix()
    {
        IReadOnlyList<string> verbKeys = RegistryVerbs.BuildVerbKeyPaths();

        Assert.Equal(3, verbKeys.Count);
        Assert.All(verbKeys, k => Assert.EndsWith(@"\shell\FileManagerRun", k));
        Assert.DoesNotContain(verbKeys, k => k.EndsWith(@"\command", StringComparison.Ordinal));
    }

    [Fact]
    public void RegistryVerbs_EmptyLauncher_Throws() =>
        Assert.Throws<ArgumentException>(() => RegistryVerbs.BuildEntries("  "));

    [Fact]
    public void ExplorerVerb_Title_IsStable() =>
        Assert.Equal("Run FileManager…", ExplorerVerb.Title);

    [Fact]
    public void ExplorerVerb_BuildInvocation_QuotesPathAndAddsManual()
    {
        string invocation = ExplorerVerb.BuildInvocation(Launcher, @"C:\data\report.txt");

        Assert.Contains($"\"{Launcher}\"", invocation);
        Assert.Contains("\"C:\\data\\report.txt\"", invocation);
        Assert.Contains("--manual", invocation);
    }

    [Fact]
    public void ExplorerVerb_BuildInvocation_EmptyLauncher_Throws() =>
        Assert.Throws<ArgumentException>(() => ExplorerVerb.BuildInvocation("", "x"));
}
