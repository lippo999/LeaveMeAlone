using FirewallBundleBlocker.Models;
using Xunit;

namespace FirewallBundleBlocker.Tests;

public class DomainTests
{
    [Fact]
    public void NewTargets_HaveUniqueIds()
    {
        var a = new FirewallTarget();
        var b = new FirewallTarget();
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Bundle_With_DoesNotMutateOriginal()
    {
        var original = new Bundle { Name = "Steam", Enabled = false };

        var updated = original with { Enabled = true };

        Assert.False(original.Enabled);
        Assert.True(updated.Enabled);
        Assert.Equal(original.Id, updated.Id);
    }

    [Fact]
    public void AddingTarget_ProducesNewList_LeavingOriginalUnchanged()
    {
        var original = new Bundle { Name = "Games" };
        var target = new FirewallTarget { Type = TargetType.Program, Value = @"C:\steam.exe" };

        var updated = original with { Targets = original.Targets.Append(target).ToList() };

        Assert.Empty(original.Targets);
        Assert.Single(updated.Targets);
    }
}
