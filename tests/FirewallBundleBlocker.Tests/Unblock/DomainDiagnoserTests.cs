using System.Net;
using FirewallBundleBlocker.Services.Unblock;
using Xunit;

namespace FirewallBundleBlocker.Tests.Unblock;

public class DomainDiagnoserTests
{
    private const string Domain = "store.steampowered.com";
    private readonly FakeResolver _system = new();
    private readonly FakeResolver _doh = new();
    private readonly FakeProbe _probe = new();

    private Task<DomainDiagnosis> Diagnose(string domain = Domain)
        => new DomainDiagnoser(_system, _doh, _probe).DiagnoseAsync(domain);

    [Fact]
    public async Task NotBlocked_WhenDnsAgreesAndTlsWorks()
    {
        _system.Answer(Domain, "104.83.198.104");
        _doh.Answer(Domain, "104.83.198.104");
        _probe.Server("104.83.198.104", 50, TlsOutcome.Ok, TlsOutcome.Ok);

        var result = await Diagnose();

        Assert.Equal(BlockKind.None, result.Kind);
        Assert.Equal(new[] { IPAddress.Parse("104.83.198.104") }, result.UsableAddresses);
    }

    [Fact]
    public async Task DnsBlocked_WhenSystemDnsReturnsLoopback()
    {
        _system.Answer(Domain, "127.0.0.1");
        _doh.Answer(Domain, "104.83.198.104");
        _probe.Server("104.83.198.104", 50, TlsOutcome.Ok, TlsOutcome.Ok);

        var result = await Diagnose();

        Assert.Equal(BlockKind.Dns, result.Kind);
        Assert.Contains("127.0.0.1", result.Detail);
    }

    [Fact]
    public async Task DnsBlocked_WhenSystemDnsReturnsNothing()
    {
        _system.Fail(Domain, "timeout");
        _doh.Answer(Domain, "104.83.198.104");
        _probe.Server("104.83.198.104", 50, TlsOutcome.Ok, TlsOutcome.Ok);

        Assert.Equal(BlockKind.Dns, (await Diagnose()).Kind);
    }

    [Fact]
    public async Task DnsBlocked_WhenSystemIpIsAPublicImpostor()
    {
        _system.Answer(Domain, "203.0.113.66");
        _doh.Answer(Domain, "104.83.198.104");
        _probe.Server("203.0.113.66", 5, TlsOutcome.CertificateMismatch, TlsOutcome.CertificateMismatch)
              .Server("104.83.198.104", 50, TlsOutcome.Ok, TlsOutcome.Ok);

        Assert.Equal(BlockKind.Dns, (await Diagnose()).Kind);
    }

    [Fact]
    public async Task NotDnsBlocked_WhenSystemReturnsADifferentGenuineEdge()
    {
        _system.Answer(Domain, "23.1.1.1");
        _doh.Answer(Domain, "104.83.198.104");
        _probe.Server("23.1.1.1", 5, TlsOutcome.Ok, TlsOutcome.Ok)
              .Server("104.83.198.104", 50, TlsOutcome.Ok, TlsOutcome.Ok);

        Assert.Equal(BlockKind.None, (await Diagnose()).Kind);
    }

    [Fact]
    public async Task SniBlocked_WhenPlainHandshakeIsResetButFragmentedWorks()
    {
        _system.Answer(Domain, "104.83.198.104");
        _doh.Answer(Domain, "104.83.198.104");
        _probe.Server("104.83.198.104", 50, TlsOutcome.Reset, TlsOutcome.Ok);

        var result = await Diagnose();

        Assert.Equal(BlockKind.Sni, result.Kind);
        Assert.True(result.SniBlocked);
        Assert.Single(result.UsableAddresses);
    }

    [Fact]
    public async Task DnsAndSni_MatchesTheObservedIspBehaviour()
    {
        _system.Answer(Domain, "127.0.0.1");
        _doh.Answer(Domain, "104.83.198.104");
        _probe.Server("104.83.198.104", 50, TlsOutcome.Reset, TlsOutcome.Ok);

        var result = await Diagnose();

        Assert.Equal(BlockKind.DnsAndSni, result.Kind);
        Assert.True(result.DnsBlocked);
        Assert.True(result.SniBlocked);
    }

    [Fact]
    public async Task Other_WhenTlsFailsEvenFragmented()
    {
        _system.Answer(Domain, "104.83.198.104");
        _doh.Answer(Domain, "104.83.198.104");
        _probe.Server("104.83.198.104", 50, TlsOutcome.Reset, TlsOutcome.Reset);

        var result = await Diagnose();

        Assert.Equal(BlockKind.Other, result.Kind);
        Assert.Empty(result.UsableAddresses);
    }

    [Fact]
    public async Task Other_WhenNoRealIpIsReachable()
    {
        _system.Answer(Domain, "127.0.0.1");
        _doh.Answer(Domain, "104.83.198.104"); // probe knows no servers → TCP fails

        var result = await Diagnose();

        Assert.Equal(BlockKind.Other, result.Kind);
        Assert.Contains("port 443", result.Detail);
    }

    [Fact]
    public async Task Other_WhenDohCannotResolve()
    {
        _system.Answer(Domain, "127.0.0.1");
        _doh.Fail(Domain, "DoH unreachable");

        var result = await Diagnose();

        Assert.Equal(BlockKind.Other, result.Kind);
        Assert.Contains("DoH unreachable", result.Detail);
    }

    [Fact]
    public async Task Other_WhenEveryRealIpServesAnotherCertificate()
    {
        _system.Answer(Domain, "104.83.198.104");
        _doh.Answer(Domain, "104.83.198.104");
        _probe.Server("104.83.198.104", 50, TlsOutcome.CertificateMismatch, TlsOutcome.CertificateMismatch);

        Assert.Equal(BlockKind.Other, (await Diagnose()).Kind);
    }

    [Fact]
    public async Task Ranked_FastestFirst_UnreachableLast()
    {
        _system.Answer(Domain, "127.0.0.1");
        _doh.Answer(Domain, "1.1.1.10", "1.1.1.20", "1.1.1.30");
        _probe.Server("1.1.1.10", 120, TlsOutcome.Ok, TlsOutcome.Ok)
              .Server("1.1.1.30", 15, TlsOutcome.Ok, TlsOutcome.Ok);

        var result = await Diagnose();

        Assert.Equal(new[] { "1.1.1.30", "1.1.1.10", "1.1.1.20" }, result.Ranked.Select(r => r.Address.ToString()));
        Assert.False(result.Ranked[2].Reachable);
        Assert.Equal(new[] { "1.1.1.30", "1.1.1.10" }, result.UsableAddresses.Select(a => a.ToString()));
    }

    [Fact]
    public async Task OnlyTheFastestAddressesGetATlsCheck()
    {
        var ips = Enumerable.Range(1, 6).Select(i => $"1.1.1.{i}").ToArray();
        _system.Answer(Domain, ips[0]);
        _doh.Answer(Domain, ips);
        foreach (var (ip, i) in ips.Select((ip, i) => (ip, i)))
            _probe.Server(ip, 10 + i, TlsOutcome.Ok, TlsOutcome.Ok);

        var result = await Diagnose();

        Assert.Equal(DomainDiagnoser.TlsCheckedAddresses, _probe.TlsCalls.Count);
        Assert.Equal(TlsOutcome.NotTested, result.Ranked[^1].Tls);
    }

    [Fact]
    public async Task NormalizesTheDomain()
    {
        _system.Answer(Domain, "104.83.198.104");
        _doh.Answer(Domain, "104.83.198.104");
        _probe.Server("104.83.198.104", 50, TlsOutcome.Ok, TlsOutcome.Ok);

        var result = await Diagnose("https://Store.SteamPowered.com/app/1");

        Assert.Equal(Domain, result.Domain);
    }
}
