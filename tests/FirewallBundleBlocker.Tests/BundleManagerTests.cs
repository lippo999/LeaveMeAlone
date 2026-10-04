using System.IO;
using FirewallBundleBlocker.Models;
using FirewallBundleBlocker.Services;
using Xunit;

namespace FirewallBundleBlocker.Tests;

public class BundleManagerTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"fbb-mgr-{Guid.NewGuid():N}", "bundles.json");
    private readonly FakeFirewallService _firewall = new();

    private BundleManager NewManager() => new(new JsonBundleStore(_path), _firewall);

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task CreateBundle_PersistsAndStartsDisabled()
    {
        var bundle = await NewManager().CreateBundleAsync("Games");
        Assert.False(bundle.Enabled);

        var reloaded = await NewManager().InitializeAsync();
        Assert.Single(reloaded);
        Assert.Equal("Games", reloaded[0].Name);
    }

    [Fact]
    public async Task AddTarget_AppliesRuleAndPersists()
    {
        var manager = NewManager();
        var bundle = await manager.CreateBundleAsync("Steam");
        var target = new FirewallTarget { Type = TargetType.Program, Value = @"C:\steam.exe" };

        var updated = await manager.AddTargetAsync(bundle.Id, target);

        Assert.Single(updated.Targets);
        Assert.Contains(_firewall.Calls, c => c == $"ApplyTarget:{bundle.Id}:{target.Id}");
    }

    [Fact]
    public async Task SetEnabled_ReappliesBundleAndPersistsState()
    {
        var manager = NewManager();
        var bundle = await manager.CreateBundleAsync("Steam");
        await manager.AddTargetAsync(bundle.Id, new FirewallTarget { Type = TargetType.Program, Value = "a" });

        var enabled = await manager.SetEnabledAsync(bundle.Id, true);

        Assert.True(enabled.Enabled);
        Assert.Contains(_firewall.Calls, c => c == $"ApplyBundle:{bundle.Id}");
        Assert.True((await NewManager().InitializeAsync())[0].Enabled);
    }

    [Fact]
    public async Task RemoveTarget_RemovesRuleAndFromBundle()
    {
        var manager = NewManager();
        var bundle = await manager.CreateBundleAsync("Steam");
        var target = new FirewallTarget { Type = TargetType.Program, Value = "a" };
        await manager.AddTargetAsync(bundle.Id, target);

        var updated = await manager.RemoveTargetAsync(bundle.Id, target.Id);

        Assert.Empty(updated.Targets);
        Assert.Contains(_firewall.Calls, c => c == $"RemoveTarget:{bundle.Id}:{target.Id}");
    }

    [Fact]
    public async Task DeleteBundle_RemovesRulesAndPersistedEntry()
    {
        var manager = NewManager();
        var bundle = await manager.CreateBundleAsync("Steam");

        await manager.DeleteBundleAsync(bundle.Id);

        Assert.Contains(_firewall.Calls, c => c == $"RemoveBundle:{bundle.Id}");
        Assert.Empty(await NewManager().InitializeAsync());
    }

    [Fact]
    public async Task Initialize_ReconcilesFirewallForEachBundle()
    {
        var seed = NewManager();
        var b1 = await seed.CreateBundleAsync("A");
        var b2 = await seed.CreateBundleAsync("B");

        await NewManager().InitializeAsync();

        Assert.Contains(_firewall.Calls, c => c == $"ApplyBundle:{b1.Id}");
        Assert.Contains(_firewall.Calls, c => c == $"ApplyBundle:{b2.Id}");
    }

    [Fact]
    public async Task GetLiveRuleNames_ReflectsAppliedTargets()
    {
        var manager = NewManager();
        var bundle = await manager.CreateBundleAsync("Steam");
        await manager.AddTargetAsync(bundle.Id, new FirewallTarget { Type = TargetType.Program, Value = "a" });

        Assert.Single(await manager.GetLiveRuleNamesAsync(bundle.Id));
    }
}
