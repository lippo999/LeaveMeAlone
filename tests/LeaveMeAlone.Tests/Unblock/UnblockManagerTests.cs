using System.IO;
using LeaveMeAlone.Services.Unblock;
using Xunit;

namespace LeaveMeAlone.Tests.Unblock;

public class UnblockManagerTests
{
    private const string Store = "store.steampowered.com";
    private const string Community = "steamcommunity.com";
    private const string OriginalHosts = "127.0.0.1 localhost\r\n";

    private readonly MemoryUnblockStore _store = new();
    private readonly FakeHostsFile _hosts = new(OriginalHosts);
    private readonly FakeRelayHost _relay = new();
    private readonly FakeDiagnoser _diagnoser = new FakeDiagnoser()
        .Returns(Store, BlockKind.DnsAndSni, "104.83.198.104", "104.83.198.105")
        .Returns(Community, BlockKind.Dns, "104.83.197.9")
        .Returns("example.com", BlockKind.None, "93.184.216.34")
        .Returns("blocked.example", BlockKind.Other)
        .Returns("noips.example", BlockKind.Dns);

    private UnblockManager NewManager() => new(_store, _hosts, _diagnoser, _relay);

    [Fact]
    public async Task DnsOnly_PinsRealIpsInHosts_NoRelay()
    {
        var entry = await NewManager().UnblockAsync(Community);

        Assert.Equal(UnblockMethod.HostsPin, entry.Method);
        Assert.Contains("104.83.197.9 steamcommunity.com", _hosts.Content);
        Assert.Empty(_relay.Running);
        Assert.Single(_store.Saved);
    }

    [Fact]
    public async Task SniBlocked_StartsRelayAndPointsHostsAtLoopback()
    {
        var entry = await NewManager().UnblockAsync(Store);

        Assert.Equal(UnblockMethod.SniRelay, entry.Method);
        Assert.Equal("127.77.0.1", entry.LoopbackAddress);
        Assert.Contains("127.77.0.1 store.steampowered.com", _hosts.Content);
        Assert.DoesNotContain("104.83.198.104 store", _hosts.Content);

        var route = _relay.Running[Store];
        Assert.Equal("127.77.0.1", route.ListenAddress.ToString());
        Assert.Equal(new[] { "104.83.198.104", "104.83.198.105" }, route.Upstreams.Select(a => a.ToString()));
    }

    [Fact]
    public async Task EachRelayedDomainGetsItsOwnLoopbackAddress()
    {
        var manager = NewManager();
        _diagnoser.Returns(Community, BlockKind.Sni, "104.83.197.9");

        var first = await manager.UnblockAsync(Store);
        var second = await manager.UnblockAsync(Community);

        Assert.NotEqual(first.LoopbackAddress, second.LoopbackAddress);
        Assert.Equal(2, _relay.Running.Count);
    }

    [Fact]
    public async Task Unblock_Again_RefreshesInPlaceAndKeepsLoopback()
    {
        var manager = NewManager();
        var first = await manager.UnblockAsync(Store);
        _diagnoser.Returns(Store, BlockKind.DnsAndSni, "104.83.198.200");

        var second = await manager.UnblockAsync(Store);

        Assert.Single(manager.Domains);
        Assert.Equal(first.LoopbackAddress, second.LoopbackAddress);
        Assert.Equal("104.83.198.200", _relay.Running[Store].Upstreams.Single().ToString());
    }

    [Fact]
    public async Task Unblock_SwitchingFromRelayToPin_StopsRelay()
    {
        var manager = NewManager();
        await manager.UnblockAsync(Store);
        _diagnoser.Returns(Store, BlockKind.Dns, "104.83.198.104");

        await manager.UnblockAsync(Store);

        Assert.Empty(_relay.Running);
        Assert.Contains("104.83.198.104 store.steampowered.com", _hosts.Content);
    }

    [Theory]
    [InlineData("example.com", "isn't blocked")]
    [InlineData("blocked.example", "Can't unblock")]
    [InlineData("noips.example", "No working IP")]
    public async Task Unblock_RefusesWhenItCannotHelp_AndChangesNothing(string domain, string message)
    {
        var ex = await Assert.ThrowsAsync<UnblockException>(() => NewManager().UnblockAsync(domain));

        Assert.Contains(message, ex.Message);
        Assert.Equal(OriginalHosts, _hosts.Content);
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public async Task Unblock_RefusesWhenUserAlreadyHasAHostsEntry()
    {
        _hosts.Content = OriginalHosts + "1.2.3.4 steamcommunity.com\r\n";

        var ex = await Assert.ThrowsAsync<UnblockException>(() => NewManager().UnblockAsync(Community));

        Assert.Contains("already has its own entry", ex.Message);
        Assert.Equal(0, _hosts.Writes);
    }

    [Fact]
    public async Task Remove_RestoresHostsByteForByte_AndStopsRelay()
    {
        var manager = NewManager();
        await manager.UnblockAsync(Store);
        await manager.UnblockAsync(Community);

        await manager.RemoveAsync(Store);
        Assert.Empty(_relay.Running);
        Assert.DoesNotContain(Store, _hosts.Content);

        await manager.RemoveAsync(Community);
        Assert.Equal(OriginalHosts, _hosts.Content);
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public async Task RemoveAll_UndoesEverything()
    {
        var manager = NewManager();
        await manager.UnblockAsync(Store);
        await manager.UnblockAsync(Community);

        await manager.RemoveAllAsync();

        Assert.Equal(OriginalHosts, _hosts.Content);
        Assert.Empty(_relay.Running);
        Assert.Empty(_store.Saved);
        Assert.Empty(manager.Domains);
    }

    [Fact]
    public async Task Remove_UnknownDomain_Throws()
    {
        await Assert.ThrowsAsync<UnblockException>(() => NewManager().RemoveAsync("nothing.example"));
    }

    [Fact]
    public async Task HostsWriteFailure_RollsBackRelay_AndKeepsState()
    {
        var manager = NewManager();
        _hosts.FailWritesWith = new UnblockException("Administrator rights are required");

        await Assert.ThrowsAsync<UnblockException>(() => manager.UnblockAsync(Store));

        Assert.Empty(_relay.Running);
        Assert.Empty(manager.Domains);
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public async Task StoreSaveFailure_RestoresHosts()
    {
        var manager = NewManager();
        _store.FailSaves = true;

        await Assert.ThrowsAsync<IOException>(() => manager.UnblockAsync(Community));

        Assert.Equal(OriginalHosts, _hosts.Content);
        Assert.Empty(manager.Domains);
    }

    [Fact]
    public async Task Shutdown_RemovesRelayLinesButKeepsPins()
    {
        var manager = NewManager();
        await manager.UnblockAsync(Store);
        await manager.UnblockAsync(Community);

        await manager.ShutdownAsync();

        Assert.Empty(_relay.Running);
        Assert.DoesNotContain(Store, _hosts.Content);
        Assert.Contains("104.83.197.9 steamcommunity.com", _hosts.Content);
        Assert.Equal(2, _store.Saved.Count); // still remembered for next launch
    }

    [Fact]
    public async Task Initialize_RestartsRelaysAndRewritesHosts()
    {
        await NewManager().UnblockAsync(Store);
        await NewManager().ShutdownAsync();
        _relay.StopAll();

        var warnings = await NewManager().InitializeAsync();

        Assert.Empty(warnings);
        Assert.True(_relay.Running.ContainsKey(Store));
        Assert.Contains("127.77.0.1 store.steampowered.com", _hosts.Content);
    }

    [Fact]
    public async Task Initialize_RelayThatCannotStart_IsWarnedAndLeftOutOfHosts()
    {
        var manager = NewManager();
        await manager.UnblockAsync(Store);
        await manager.UnblockAsync(Community);
        await manager.ShutdownAsync();
        _relay.FailToStart.Add(Store);

        var restarted = NewManager();
        var warnings = await restarted.InitializeAsync();

        Assert.Contains(warnings, w => w.Contains("port busy"));
        Assert.DoesNotContain(Store, _hosts.Content);
        Assert.Contains(Community, _hosts.Content);
        Assert.Equal(2, restarted.Domains.Count);
    }

    [Fact]
    public async Task Initialize_NothingSaved_DoesNotTouchHosts()
    {
        await NewManager().InitializeAsync();
        Assert.Equal(0, _hosts.Writes);
    }
}
