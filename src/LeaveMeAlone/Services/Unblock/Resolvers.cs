using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace LeaveMeAlone.Services.Unblock;

public interface IDnsResolver
{
    Task<DnsAnswer> ResolveAsync(string domain, CancellationToken ct = default);
}

/// <summary>
/// Asks the DNS servers configured on this PC's network adapters (i.e. what the ISP answers),
/// bypassing the hosts file so our own pins never mask the real answer.
/// </summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<DnsAnswer> ResolveAsync(string domain, CancellationToken ct = default)
    {
        var servers = ConfiguredServers();
        if (servers.Count == 0) return DnsAnswer.Failed("No DNS servers are configured on any active network adapter.");

        string? lastError = null;
        foreach (var server in servers)
        {
            try
            {
                return new DnsAnswer(await QueryAsync(server, domain, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is SocketException or FormatException or TimeoutException)
            {
                lastError = $"{server}: {ex.Message}";
            }
        }
        return DnsAnswer.Failed(lastError ?? "No DNS server answered.");
    }

    private static async Task<IReadOnlyList<IPAddress>> QueryAsync(IPAddress server, string domain, CancellationToken ct)
    {
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);
        using var udp = new UdpClient(server.AddressFamily);
        await udp.SendAsync(DnsWire.BuildQuery(id, domain), new IPEndPoint(server, 53), ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var reply = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            return DnsWire.ParseAddresses(reply.Buffer, id);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("no reply");
        }
    }

    private static IReadOnlyList<IPAddress> ConfiguredServers() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().DnsAddresses)
        // fec0:0:0:ffff::/48 are Windows' placeholder "site-local" resolvers that never answer.
        .Where(a => !a.IsIPv6SiteLocal)
        .Distinct()
        .ToList();
}

/// <summary>
/// DNS-over-HTTPS lookups used to learn a domain's real IPs. Endpoints are IP literals so the
/// lookup itself can't be poisoned; only the domain name is sent, nothing else about the user.
/// </summary>
public sealed class DohResolver : IDnsResolver
{
    public static readonly IReadOnlyList<string> DefaultEndpoints = new[]
    {
        "https://1.1.1.1/dns-query?name={0}&type=A", // Cloudflare
        "https://8.8.8.8/resolve?name={0}&type=A",   // Google
    };

    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _endpoints;

    public DohResolver(HttpClient? http = null, IReadOnlyList<string>? endpoints = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _endpoints = endpoints ?? DefaultEndpoints;
    }

    /// <summary>Union of all providers' answers, so CDN domains yield every edge each provider knows.</summary>
    public async Task<DnsAnswer> ResolveAsync(string domain, CancellationToken ct = default)
    {
        var results = await Task.WhenAll(_endpoints.Select(e => QueryAsync(e, domain, ct))).ConfigureAwait(false);
        var addresses = results.SelectMany(r => r.Addresses).Distinct().ToList();
        if (addresses.Count > 0) return new DnsAnswer(addresses);

        var errors = results.Where(r => r.Error != null).Select(r => r.Error);
        return DnsAnswer.Failed(string.Join("; ", errors) is { Length: > 0 } msg ? msg : "No A records.");
    }

    private async Task<DnsAnswer> QueryAsync(string endpoint, string domain, CancellationToken ct)
    {
        var url = string.Format(endpoint, Uri.EscapeDataString(domain));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new DnsAnswer(ParseJson(json));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            return DnsAnswer.Failed($"{new Uri(url).Host}: {ex.Message}");
        }
    }

    /// <summary>Parses the JSON DoH format shared by Cloudflare and Google; returns the A records.</summary>
    public static IReadOnlyList<IPAddress> ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
            return Array.Empty<IPAddress>();

        var result = new List<IPAddress>();
        foreach (var answer in answers.EnumerateArray())
        {
            if (answer.TryGetProperty("type", out var type) && type.GetInt32() == 1
                && answer.TryGetProperty("data", out var data)
                && IPAddress.TryParse(data.GetString(), out var ip))
            {
                result.Add(ip);
            }
        }
        return result;
    }
}
