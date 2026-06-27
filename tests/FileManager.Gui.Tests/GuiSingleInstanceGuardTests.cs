using FileManager.Gui;
using Xunit;

namespace FileManager.Gui.Tests;

/// <summary>
/// Verifies the GUI single-instance guard (MAJOR-3): the first claimant is primary; a second claimant on
/// the SAME per-user name is NOT primary (so a cold-started GUI exits cleanly, leaving one subscriber);
/// and releasing the first lets a later claimant become primary. Uses a unique name per test so it is
/// deterministic and never collides with a real GUI or another test — cross-platform (named mutex).
/// </summary>
public sealed class GuiSingleInstanceGuardTests
{
    [Fact]
    public void FirstClaimant_IsPrimary_SecondIsNot()
    {
        string name = Unique();
        using GuiSingleInstanceGuard first = GuiSingleInstanceGuard.Acquire(name);
        Assert.True(first.IsPrimaryInstance);

        using GuiSingleInstanceGuard second = GuiSingleInstanceGuard.Acquire(name);
        Assert.False(second.IsPrimaryInstance);
    }

    [Fact]
    public void Release_AllowsLaterClaimant_ToBecomePrimary()
    {
        string name = Unique();

        GuiSingleInstanceGuard first = GuiSingleInstanceGuard.Acquire(name);
        Assert.True(first.IsPrimaryInstance);
        first.Dispose();

        using GuiSingleInstanceGuard later = GuiSingleInstanceGuard.Acquire(name);
        Assert.True(later.IsPrimaryInstance);
    }

    [Fact]
    public void DistinctNames_BothPrimary()
    {
        using GuiSingleInstanceGuard a = GuiSingleInstanceGuard.Acquire(Unique());
        using GuiSingleInstanceGuard b = GuiSingleInstanceGuard.Acquire(Unique());
        Assert.True(a.IsPrimaryInstance);
        Assert.True(b.IsPrimaryInstance);
    }

    private static string Unique() => "fmgui-test-" + Guid.NewGuid().ToString("N");
}
