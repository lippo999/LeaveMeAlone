using System.IO;
using System.Net;
using FirewallBundleBlocker.Services.Unblock;

namespace FirewallBundleBlocker.Tests.Unblock;

public sealed class FakeResolver : IDnsResolver
{
    private readonly Dictionary<string, DnsAnswer> _answers = new();

    public FakeResolver Answer(string domain, params string[] addresses)
    {
        _answers[domain] = new DnsAnswer(addresses.Select(IPAddress.Parse).ToList());
        return this;
    }

    public FakeResolver Fail(string domain, string error)
    {
        _answers[domain] = DnsAnswer.Failed(error);
        return this;
    }

    public Task<DnsAnswer> ResolveAsync(string domain, CancellationToken ct = default)
        => Task.FromResult(_answers.TryGetValue(domain, out var a) ? a : DnsAnswer.Failed("no answer"));
}

/// <summary>Scripted network: per-IP TCP latency and TLS outcome with/without ClientHello fragmentation.</summary>
public sealed class FakeProbe : IConnectivityProbe
{
    private readonly Dictionary<IPAddress, TimeSpan> _latency = new();
    private readonly Dictionary<(IPAddress, bool), TlsOutcome> _tls = new();
    public List<string> TlsCalls { get; } = new();

    public FakeProbe Server(string ip, int latencyMs, TlsOutcome plain, TlsOutcome fragmented)
    {
        var address = IPAddress.Parse(ip);
        _latency[address] = TimeSpan.FromMilliseconds(latencyMs);
        _tls[(address, false)] = plain;
        _tls[(address, true)] = fragmented;
        return this;
    }

    public Task<TimeSpan?> TcpConnectAsync(IPAddress address, CancellationToken ct = default)
        => Task.FromResult(_latency.TryGetValue(address, out var l) ? l : (TimeSpan?)null);

    public Task<TlsProbeResult> TlsHandshakeAsync(IPAddress address, string host, bool fragmentClientHello, CancellationToken ct = default)
    {
        lock (TlsCalls) TlsCalls.Add($"{address}:{(fragmentClientHello ? "frag" : "plain")}");
        var outcome = _tls.TryGetValue((address, fragmentClientHello), out var o) ? o : TlsOutcome.ConnectFailed;
        return Task.FromResult(new TlsProbeResult(outcome, TimeSpan.FromMilliseconds(10)));
    }
}

public sealed class FakeHostsFile : IHostsFile
{
    public FakeHostsFile(string content = "127.0.0.1 localhost\r\n") => Content = content;

    public string Content { get; set; }
    public int Writes { get; private set; }
    public Exception? FailWritesWith { get; set; }

    public Task<string> ReadAsync(CancellationToken ct = default) => Task.FromResult(Content);

    public Task WriteAsync(string content, CancellationToken ct = default)
    {
        if (FailWritesWith != null) throw FailWritesWith;
        Writes++;
        Content = content;
        return Task.CompletedTask;
    }
}

public sealed class FakeRelayHost : IRelayHost
{
    public Dictionary<string, RelayRoute> Running { get; } = new();
    public HashSet<string> FailToStart { get; } = new();

    public void Start(RelayRoute route)
    {
        if (FailToStart.Contains(route.Domain)) throw new UnblockException($"port busy for {route.Domain}");
        Running[route.Domain] = route;
    }

    public void Stop(string domain) => Running.Remove(domain);
    public void StopAll() => Running.Clear();

    public IReadOnlyList<RelayStatus> Status()
        => Running.Keys.Select(d => new RelayStatus(d, true, 0, null)).ToList();

    public void Dispose() => StopAll();
}

public sealed class FakeDiagnoser : IDomainDiagnoser
{
    private readonly Dictionary<string, DomainDiagnosis> _results = new();

    public FakeDiagnoser Returns(string domain, BlockKind kind, params string[] usable)
    {
        var ranked = usable.Select((ip, i) => new AddressProbe(IPAddress.Parse(ip), TimeSpan.FromMilliseconds(10 + i), TlsOutcome.Ok)).ToList();
        _results[domain] = new DomainDiagnosis(domain, kind, Array.Empty<IPAddress>(),
            ranked.Select(r => r.Address).ToList(), ranked, $"{kind} detail");
        return this;
    }

    public Task<DomainDiagnosis> DiagnoseAsync(string domain, CancellationToken ct = default)
        => Task.FromResult(_results[DomainName.Normalize(domain)]);
}

public sealed class MemoryUnblockStore : IUnblockStore
{
    public IReadOnlyList<UnblockedDomain> Saved { get; set; } = Array.Empty<UnblockedDomain>();
    public bool FailSaves { get; set; }

    public Task<IReadOnlyList<UnblockedDomain>> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved);

    public Task SaveAsync(IReadOnlyList<UnblockedDomain> domains, CancellationToken ct = default)
    {
        if (FailSaves) throw new IOException("disk full");
        Saved = domains.ToList();
        return Task.CompletedTask;
    }
}
