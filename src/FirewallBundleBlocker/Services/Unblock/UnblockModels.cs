using System.Globalization;
using System.Net;

namespace FirewallBundleBlocker.Services.Unblock;

/// <summary>How the ISP interferes with a domain, as found by <see cref="DomainDiagnoser"/>.</summary>
public enum BlockKind
{
    /// <summary>System DNS returns real addresses and a normal TLS handshake succeeds.</summary>
    None,

    /// <summary>System DNS returns a fake/bogus address; the real IPs work over normal TLS.</summary>
    Dns,

    /// <summary>DNS is fine but the TLS connection is reset when the ClientHello carries this SNI.</summary>
    Sni,

    /// <summary>Both DNS poisoning and SNI filtering.</summary>
    DnsAndSni,

    /// <summary>Blocked some other way (IP blocking, TLS blocked even when fragmented, no real IP found…).</summary>
    Other,
}

/// <summary>How an unblocked domain is being routed around the filter.</summary>
public enum UnblockMethod
{
    /// <summary>A hosts-file entry pins the domain to its real IPs (beats DNS poisoning only).</summary>
    HostsPin,

    /// <summary>
    /// A hosts-file entry points the domain at a private loopback address where the app relays the
    /// connection to the real IP, splitting the TLS ClientHello so the ISP's DPI can't read the SNI.
    /// Encryption stays end-to-end between the browser and the real server.
    /// </summary>
    SniRelay,
}

/// <summary>Outcome of a single TLS handshake attempt.</summary>
public enum TlsOutcome
{
    Ok,
    /// <summary>Handshake completed but the certificate doesn't match the domain (wrong/fake server).</summary>
    CertificateMismatch,
    /// <summary>Connection was reset or closed during the handshake (typical of SNI filtering).</summary>
    Reset,
    Timeout,
    ConnectFailed,
    NotTested,
}

public sealed record TlsProbeResult(TlsOutcome Outcome, TimeSpan Elapsed, string? Error = null);

/// <summary>Measurements for one real IP of a domain.</summary>
public sealed record AddressProbe(IPAddress Address, TimeSpan? TcpLatency, TlsOutcome Tls)
{
    public bool Reachable => TcpLatency.HasValue;
}

public sealed record DnsAnswer(IReadOnlyList<IPAddress> Addresses, string? Error = null)
{
    public static DnsAnswer Failed(string error) => new(Array.Empty<IPAddress>(), error);
}

public sealed record DomainDiagnosis(
    string Domain,
    BlockKind Kind,
    IReadOnlyList<IPAddress> SystemAddresses,
    IReadOnlyList<IPAddress> RealAddresses,
    IReadOnlyList<AddressProbe> Ranked, // real IPs, fastest first; unreachable ones last
    string Detail)
{
    public bool DnsBlocked => Kind is BlockKind.Dns or BlockKind.DnsAndSni;
    public bool SniBlocked => Kind is BlockKind.Sni or BlockKind.DnsAndSni;

    /// <summary>Reachable addresses whose TLS check passed, fastest first.</summary>
    public IReadOnlyList<IPAddress> UsableAddresses => Ranked
        .Where(p => p.Reachable && p.Tls is TlsOutcome.Ok)
        .Select(p => p.Address)
        .ToList();
}

/// <summary>A domain the app is currently unblocking. Immutable; persisted to unblocked.json.</summary>
public sealed record UnblockedDomain
{
    public string Domain { get; init; } = "";
    public UnblockMethod Method { get; init; }
    public BlockKind DetectedKind { get; init; }

    /// <summary>Real upstream IPs, fastest first (written to hosts for HostsPin; relay targets for SniRelay).</summary>
    public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();

    /// <summary>Private loopback address the relay listens on (SniRelay only).</summary>
    public string? LoopbackAddress { get; init; }

    public DateTimeOffset UnblockedAt { get; init; }
}

public sealed class UnblockException : Exception
{
    public UnblockException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Normalizes user input ("https://Store.SteamPowered.com/app/1") into a bare host name.</summary>
public static class DomainName
{
    private static readonly IdnMapping Idn = new();

    public static bool TryNormalize(string? input, out string domain)
    {
        domain = "";
        var text = input?.Trim() ?? "";
        if (text.Length == 0) return false;

        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host.TrimEnd('.');
        if (uri.HostNameType != UriHostNameType.Dns || !host.Contains('.')) return false;

        try { domain = Idn.GetAscii(host).ToLowerInvariant(); }
        catch (ArgumentException) { return false; }
        return true;
    }

    public static string Normalize(string? input) => TryNormalize(input, out var domain)
        ? domain
        : throw new UnblockException($"'{input}' is not a valid domain name.");
}
