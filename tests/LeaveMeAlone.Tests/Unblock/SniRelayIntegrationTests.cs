using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LeaveMeAlone.Services.Unblock;
using Xunit;

namespace LeaveMeAlone.Tests.Unblock;

/// <summary>
/// Real sockets on loopback: proves a TLS server (SChannel) accepts the split ClientHello, and that
/// the relay forwards a full HTTPS exchange end-to-end without touching the encryption.
/// </summary>
public class SniRelayIntegrationTests : IDisposable
{
    private const string Host = "relay-test.example";
    private static readonly IPAddress RelayAddress = IPAddress.Parse("127.77.0.250");
    private readonly X509Certificate2 _certificate = CreateCertificate(Host);
    private readonly TcpListener _server = new(IPAddress.Loopback, 0);

    public SniRelayIntegrationTests() => _server.Start();

    private int Port => ((IPEndPoint)_server.LocalEndpoint).Port;

    public void Dispose()
    {
        _server.Stop();
        _certificate.Dispose();
    }

    [Fact]
    public async Task FragmentingStream_HandshakeWithRealTlsServer_Succeeds()
    {
        var serverTask = ServeOneAsync(echoLine: true);

        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, Port);
        using var ssl = new SslStream(new ClientHelloFragmentingStream(tcp.GetStream(), Host), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(Host);

        Assert.Equal("pong", await RoundTripAsync(ssl, "ping"));
        await serverTask;
    }

    [Fact]
    public async Task Relay_ForwardsHttpsEndToEnd()
    {
        using var relay = new SniRelayHost(httpsPort: Port, httpPort: FreePort());
        relay.Start(new RelayRoute(Host, RelayAddress, new[] { IPAddress.Loopback }));
        var serverTask = ServeOneAsync(echoLine: true);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(RelayAddress, Port);
        using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert!.Subject.Contains(Host));
        await ssl.AuthenticateAsClientAsync(Host);

        Assert.Equal("pong", await RoundTripAsync(ssl, "ping"));
        await serverTask;
        Assert.Equal(1, relay.Status().Single().Connections);
    }

    [Fact]
    public async Task Relay_SplitsTheClientHelloIntoTwoRecords()
    {
        using var relay = new SniRelayHost(httpsPort: Port, httpPort: FreePort());
        relay.Start(new RelayRoute(Host, RelayAddress, new[] { IPAddress.Loopback }));
        var captured = CaptureFirstRecordsAsync();

        var hello = TlsFragmenterTests.FakeClientHello(Host);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(RelayAddress, Port);
        await tcp.GetStream().WriteAsync(hello);

        var records = await captured;
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(0x16, r[0]));
        Assert.Equal(hello[5..], records[0][5..].Concat(records[1][5..]).ToArray());
    }

    [Fact]
    public void Start_PortAlreadyTaken_ThrowsUnblockException()
    {
        using var blocker = new TcpListener(RelayAddress, Port);
        blocker.Start();
        using var relay = new SniRelayHost(httpsPort: Port, httpPort: FreePort());

        var ex = Assert.Throws<UnblockException>(() => relay.Start(new RelayRoute(Host, RelayAddress, new[] { IPAddress.Loopback })));
        Assert.Contains(RelayAddress.ToString(), ex.Message);
    }

    [Fact]
    public async Task Stop_ClosesTheListener()
    {
        var relay = new SniRelayHost(httpsPort: Port, httpPort: FreePort());
        relay.Start(new RelayRoute(Host, RelayAddress, new[] { IPAddress.Loopback }));
        relay.Stop(Host);

        Assert.Empty(relay.Status());
        using var tcp = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => tcp.ConnectAsync(RelayAddress, Port));
    }

    // Accepts one TLS client and answers "ping" with "pong". The SNI isn't asserted: .NET's own
    // server-side SNI sniffer reads only the first record and returns a garbled name for a split
    // ClientHello — the same blind spot the ISP's DPI has — while SChannel completes the handshake fine.
    private async Task ServeOneAsync(bool echoLine)
    {
        using var client = await _server.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var ssl = new SslStream(client.GetStream());
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate });
        if (echoLine)
        {
            var buffer = new byte[64];
            await ssl.ReadAsync(buffer);
            await ssl.WriteAsync(Encoding.ASCII.GetBytes("pong\n"));
        }
    }

    // Reads exactly two TLS records off the upstream side of the relay.
    private async Task<List<byte[]>> CaptureFirstRecordsAsync()
    {
        using var client = await _server.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var stream = client.GetStream();
        var records = new List<byte[]>();
        for (int i = 0; i < 2; i++)
        {
            var header = new byte[5];
            await stream.ReadExactlyAsync(header);
            var body = new byte[(header[3] << 8) | header[4]];
            await stream.ReadExactlyAsync(body);
            records.Add(header.Concat(body).ToArray());
        }
        return records;
    }

    private static async Task<string> RoundTripAsync(SslStream ssl, string message)
    {
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(message + "\n"));
        var buffer = new byte[64];
        int n = await ssl.ReadAsync(buffer);
        return Encoding.ASCII.GetString(buffer, 0, n).Trim();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static X509Certificate2 CreateCertificate(string host)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now.AddHours(1));
        // SChannel needs a persisted key; round-trip through PFX.
        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
    }
}
