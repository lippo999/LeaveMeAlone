using System.Net;

namespace LeaveMeAlone.Services.Unblock;

public interface IDomainDiagnoser
{
    Task<DomainDiagnosis> DiagnoseAsync(string domain, CancellationToken ct = default);
}

/// <summary>
/// Works out how a domain is blocked:
/// 1. DNS — compare the system resolver's answer with DoH; a bogus/forged answer means poisoning.
/// 2. Rank the real IPs by TCP connect time.
/// 3. SNI — a normal TLS handshake to a real IP is reset, but the same handshake with a
///    fragmented ClientHello succeeds.
/// Anything else (no reachable IP, TLS fails even fragmented) is reported as <see cref="BlockKind.Other"/>.
/// </summary>
public sealed class DomainDiagnoser : IDomainDiagnoser
{
    /// <summary>How many of the fastest IPs get a TLS check (the rest are listed by latency only).</summary>
    public const int TlsCheckedAddresses = 4;

    private readonly IDnsResolver _systemDns;
    private readonly IDnsResolver _realDns;
    private readonly IConnectivityProbe _probe;

    public DomainDiagnoser(IDnsResolver systemDns, IDnsResolver realDns, IConnectivityProbe probe)
    {
        _systemDns = systemDns;
        _realDns = realDns;
        _probe = probe;
    }

    public async Task<DomainDiagnosis> DiagnoseAsync(string domain, CancellationToken ct = default)
    {
        domain = DomainName.Normalize(domain);
        var systemTask = _systemDns.ResolveAsync(domain, ct);
        var realTask = _realDns.ResolveAsync(domain, ct);
        var system = await systemTask.ConfigureAwait(false);
        var real = await realTask.ConfigureAwait(false);

        if (real.Addresses.Count == 0)
        {
            var why = system.Addresses.Count == 0
                ? "The domain doesn't resolve anywhere — check the spelling."
                : $"Couldn't look up the real IPs over DNS-over-HTTPS ({real.Error}).";
            return Result(domain, BlockKind.Other, system, real, Array.Empty<AddressProbe>(), why);
        }

        var latencies = await RankByLatencyAsync(real.Addresses, ct).ConfigureAwait(false);
        var reachable = latencies.Where(p => p.Reachable).ToList();
        if (reachable.Count == 0)
            return Result(domain, BlockKind.Other, system, real, latencies,
                "None of the real IPs accept connections on port 443 — the ISP may be blocking the IPs themselves.");

        var dns = await CheckDnsAsync(domain, system, real, ct).ConfigureAwait(false);
        var tls = await CheckSniAsync(domain, reachable, ct).ConfigureAwait(false);
        var ranked = MergeTls(latencies, tls.Outcomes);

        if (tls.Kind == SniVerdict.Unusable)
            return Result(domain, BlockKind.Other, system, real, ranked, tls.Detail);

        var kind = (dns.Poisoned, tls.Kind == SniVerdict.Filtered) switch
        {
            (true, true) => BlockKind.DnsAndSni,
            (true, false) => BlockKind.Dns,
            (false, true) => BlockKind.Sni,
            _ => BlockKind.None,
        };
        return Result(domain, kind, system, real, ranked, $"{dns.Detail} {tls.Detail}");
    }

    private async Task<IReadOnlyList<AddressProbe>> RankByLatencyAsync(IReadOnlyList<IPAddress> addresses, CancellationToken ct)
    {
        var probes = await Task.WhenAll(addresses.Select(async a =>
        {
            // Best of two samples smooths out a single slow SYN.
            var first = await _probe.TcpConnectAsync(a, ct).ConfigureAwait(false);
            var second = first is null ? null : await _probe.TcpConnectAsync(a, ct).ConfigureAwait(false);
            var best = second is { } s && s < first ? s : first;
            return new AddressProbe(a, best, TlsOutcome.NotTested);
        })).ConfigureAwait(false);

        return probes.OrderBy(p => p.TcpLatency ?? TimeSpan.MaxValue).ToList();
    }

    private async Task<(bool Poisoned, string Detail)> CheckDnsAsync(string domain, DnsAnswer system, DnsAnswer real, CancellationToken ct)
    {
        if (system.Addresses.Count == 0)
            return (true, $"System DNS gives no answer ({system.Error ?? "empty"}) although the domain exists.");

        var bogus = system.Addresses.FirstOrDefault(AddressClassifier.IsBogon);
        if (bogus != null)
            return (true, $"System DNS returns a fake address ({bogus}) — DNS poisoning.");

        if (system.Addresses.Intersect(real.Addresses).Any())
            return (false, "System DNS answers correctly.");

        // Different IPs can just be a different CDN edge. Only call it poisoned if the system's IP
        // demonstrably isn't this domain (wrong certificate or nothing listening).
        var candidate = system.Addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        if (candidate is null)
            return (true, "System DNS returns no usable IPv4 address — DNS poisoning.");
        var check = await _probe.TlsHandshakeAsync(candidate, domain, fragmentClientHello: true, ct).ConfigureAwait(false);
        return check.Outcome is TlsOutcome.CertificateMismatch or TlsOutcome.ConnectFailed
            ? (true, $"System DNS returns {candidate}, which isn't a real server for this domain — DNS poisoning.")
            : (false, "System DNS answers with a different (but genuine) server.");
    }

    private enum SniVerdict { Clear, Filtered, Unusable }

    private sealed record SniResult(SniVerdict Kind, string Detail, IReadOnlyDictionary<IPAddress, TlsOutcome> Outcomes);

    private async Task<SniResult> CheckSniAsync(string domain, IReadOnlyList<AddressProbe> reachable, CancellationToken ct)
    {
        var candidates = reachable.Take(TlsCheckedAddresses).Select(p => p.Address).ToList();

        var plain = await ProbeAllAsync(candidates, domain, fragment: false, ct).ConfigureAwait(false);
        int working = plain.Values.Count(o => o == TlsOutcome.Ok);
        if (working == plain.Count)
            return new SniResult(SniVerdict.Clear, "A normal TLS connection works.", plain);
        if (working > 0)
            // Typical for CDNs: in-country edges pass while international ones are SNI-filtered.
            return new SniResult(SniVerdict.Clear,
                $"A normal TLS connection works on {working} of {plain.Count} tested IPs (the others are SNI-filtered); only working IPs are used.",
                plain);

        if (plain.Values.All(o => o == TlsOutcome.CertificateMismatch))
            return new SniResult(SniVerdict.Unusable,
                "The real IPs answer with a certificate for a different domain; can't unblock safely.", plain);

        var fragmented = await ProbeAllAsync(candidates, domain, fragment: true, ct).ConfigureAwait(false);
        if (fragmented.Values.Any(o => o == TlsOutcome.Ok))
            return new SniResult(SniVerdict.Filtered,
                "TLS is reset when the domain name is visible, but works with a split ClientHello — SNI filtering.", fragmented);

        return new SniResult(SniVerdict.Unusable,
            $"TLS fails even with a split ClientHello ({plain.Values.First()}); blocked some other way.", fragmented);
    }

    private async Task<IReadOnlyDictionary<IPAddress, TlsOutcome>> ProbeAllAsync(
        IReadOnlyList<IPAddress> addresses, string domain, bool fragment, CancellationToken ct)
    {
        var results = await Task.WhenAll(addresses.Select(async a =>
            (a, (await _probe.TlsHandshakeAsync(a, domain, fragment, ct).ConfigureAwait(false)).Outcome))).ConfigureAwait(false);
        return results.ToDictionary(r => r.a, r => r.Outcome);
    }

    private static IReadOnlyList<AddressProbe> MergeTls(IReadOnlyList<AddressProbe> ranked, IReadOnlyDictionary<IPAddress, TlsOutcome> tls)
        => ranked.Select(p => tls.TryGetValue(p.Address, out var o) ? p with { Tls = o } : p).ToList();

    private static DomainDiagnosis Result(string domain, BlockKind kind, DnsAnswer system, DnsAnswer real, IReadOnlyList<AddressProbe> ranked, string detail)
        => new(domain, kind, system.Addresses, real.Addresses, ranked, detail.Trim());
}
