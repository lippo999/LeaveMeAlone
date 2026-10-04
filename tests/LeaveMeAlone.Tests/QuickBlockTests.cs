using LeaveMeAlone.Models;
using LeaveMeAlone.Services;
using Xunit;

namespace LeaveMeAlone.Tests;

public class QuickBlockTests
{
    private static readonly DetectedLauncher Steam = new("Steam", new[] { @"C:\Steam\steam.exe" });
    private static readonly DetectedLauncher Epic = new("Epic Games Launcher", new[] { @"C:\Epic\Epic.exe" });

    [Fact]
    public void Launchers_come_first_and_link_to_the_bundle_with_the_same_name()
    {
        var steamBundle = new Bundle { Name = "steam", Enabled = true };
        var custom = new Bundle { Name = "LAN party" };

        var items = QuickBlock.Build(new[] { Steam, Epic }, new[] { custom, steamBundle });

        Assert.Equal(new[] { "Steam", "Epic Games Launcher", "LAN party" }, items.Select(i => i.Title));
        Assert.Same(steamBundle, items[0].Bundle);
        Assert.Null(items[1].Bundle);
        Assert.Same(Epic, items[1].Launcher);
        Assert.Null(items[2].Launcher);
    }

    [Fact]
    public void A_bundle_linked_to_a_launcher_is_not_listed_twice()
    {
        var items = QuickBlock.Build(new[] { Steam }, new[] { new Bundle { Name = "Steam" } });

        Assert.Single(items);
    }

    [Fact]
    public void Only_the_first_bundle_with_a_launcher_name_is_linked()
    {
        var first = new Bundle { Name = "Steam" };
        var second = new Bundle { Name = "Steam" };

        var items = QuickBlock.Build(new[] { Steam }, new[] { first, second });

        Assert.Equal(2, items.Count);
        Assert.Same(first, items[0].Bundle);
        Assert.Same(second, items[1].Bundle);
        Assert.Null(items[1].Launcher);
    }

    [Fact]
    public void Item_is_on_only_when_its_bundle_is_enabled()
    {
        Assert.False(new QuickBlockItem("Steam", Steam, null).IsOn);
        Assert.False(new QuickBlockItem("Steam", Steam, new Bundle { Enabled = false }).IsOn);
        Assert.True(new QuickBlockItem("Steam", Steam, new Bundle { Enabled = true }).IsOn);
    }

    [Fact]
    public void Launcher_targets_are_one_program_rule_per_detected_exe()
    {
        var launcher = new DetectedLauncher("Steam", new[] { @"C:\Steam\steam.exe", @"C:\Steam\web.exe" });

        var targets = QuickBlock.TargetsFor(launcher);

        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.Equal(TargetType.Program, t.Type));
        Assert.Equal(launcher.ExistingPaths, targets.Select(t => t.Value));
    }
}
