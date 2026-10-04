using System.IO;
using System.Net;
using System.Net.Sockets;

namespace FirewallBundleBlocker.Services.Unblock;

/// <summary>One relayed domain: connections to <see cref="ListenAddress"/> are forwarded to the upstreams.</summary>
public sealed record RelayRoute(string Domain, IPAddress ListenAddress, IReadOnlyList<IPAddress> Upstreams);

public sealed record RelayStatus(string Domain, bool Running, long Connections, string? LastError);

public interface IRelayHost : IDisposable
{
    /// <summary>Starts (or restarts) listening for <paramref name="route"/>. Throws <see cref="UnblockException"/> if the port can't be bound.</summary>
    void Start(RelayRoute route);

    void Stop(string domain);
    void StopAll();
    IReadOnlyList<RelayStatus> Status();
}

/// <summary>
/// Local TCP relay used for SNI-filtered domains. The hosts file points the domain at a private
/// loopback address (127.77.0.x); the browser connects there and its bytes are forwarded to the
/// real server unchanged, except that the TLS ClientHello is split into two records
/// (<see cref="TlsFragmenter"/>). This is not a proxy that decrypts anything: TLS stays end-to-end
/// and the browser still validates the real server's certificate.
/// Port 80 is relayed as-is so plain-HTTP redirects to HTTPS keep working.
/// </summary>
public sealed class SniRelayHost : IRelayHost
{
    public const int HttpsPort = 443;
    public const int HttpPort = 80;
    private const int MaxClientHelloBytes = 64 * 1024;
    private static readonly TimeSpan UpstreamConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClientHelloTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, Route> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _httpsPort;
    private readonly int _httpPort;

    /// <param name="httpsPort">Overridable for tests (binding 443 may be unavailable there).</param>
    public SniRelayHost(int httpsPort = HttpsPort, int httpPort = HttpPort)
    {
        _httpsPort = httpsPort;
        _httpPort = httpPort;
    }

    public void Start(RelayRoute route)
    {
        Stop(route.Domain);
        var state = new Route(route);
        try
        {
            state.Listen(_httpsPort, fragmentHello: true);
        }
        catch (SocketException ex)
        {
            state.Dispose();
            throw new UnblockException(
                $"Couldn't listen on {route.ListenAddress}:{_httpsPort} for {route.Domain}: {ex.Message}", ex);
        }

        // Best-effort: something else (IIS / http.sys) may already own port 80 on all addresses.
        try { state.Listen(_httpPort, fragmentHello: false); }
        catch (SocketException ex) { state.LastError = $"HTTP (port {_httpPort}) not relayed: {ex.Message}"; }

        lock (_gate) _routes[route.Domain] = state;
    }

    public void Stop(string domain)
    {
        Route? state;
        lock (_gate)
        {
            if (!_routes.Remove(domain, out state)) return;
        }
        state.Dispose();
    }

    public void StopAll()
    {
        List<Route> all;
        lock (_gate)
        {
            all = _routes.Values.ToList();
            _routes.Clear();
        }
        foreach (var state in all) state.Dispose();
    }

    public IReadOnlyList<RelayStatus> Status()
    {
        lock (_gate)
        {
            return _routes.Values
                .Select(r => new RelayStatus(r.Config.Domain, !r.Stopping.IsCancellationRequested, Interlocked.Read(ref r.Connections), r.LastError))
                .ToList();
        }
    }

    public void Dispose() => StopAll();

    /// <summary>Listeners + in-flight connections of one domain; cancelling <see cref="Stopping"/> tears everything down.</summary>
    private sealed class Route : IDisposable
    {
        private readonly List<TcpListener> _listeners = new();

        public Route(RelayRoute config) => Config = config;

        public RelayRoute Config { get; }
        public CancellationTokenSource Stopping { get; } = new();
        public long Connections;
        public volatile string? LastError;

        public void Listen(int port, bool fragmentHello)
        {
            var listener = new TcpListener(Config.ListenAddress, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            _listeners.Add(listener);
            _ = AcceptLoopAsync(listener, port, fragmentHello);
        }

        private async Task AcceptLoopAsync(TcpListener listener, int port, bool fragmentHello)
        {
            var ct = Stopping.Token;
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    if (!ct.IsCancellationRequested) LastError = $"Listener on port {port} stopped: {ex.Message}";
                    return;
                }

                Interlocked.Increment(ref Connections);
                _ = HandleAsync(client, port, fragmentHello, ct);
            }
        }

        private async Task HandleAsync(TcpClient client, int port, bool fragmentHello, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    using var upstream = await ConnectUpstreamAsync(port, ct).ConfigureAwait(false);
                    var downstream = client.GetStream();
                    var up = upstream.GetStream();

                    if (fragmentHello)
                        await ForwardClientHelloAsync(downstream, up, ct).ConfigureAwait(false);

                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var toServer = PumpAsync(downstream, upstream.Client, linked.Token);
                    var toClient = PumpAsync(up, client.Client, linked.Token);
                    await Task.WhenAny(toServer, toClient).ConfigureAwait(false);
                    // One side finished; give the other a moment to drain, then tear down.
                    await Task.WhenAny(Task.WhenAll(toServer, toClient), Task.Delay(TimeSpan.FromSeconds(30), ct)).ConfigureAwait(false);
                    linked.Cancel();
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or UnblockException)
                {
                    if (!ct.IsCancellationRequested) LastError = ex.Message;
                }
            }
        }

        private async Task<TcpClient> ConnectUpstreamAsync(int port, CancellationToken ct)
        {
            string? lastError = null;
            foreach (var address in Config.Upstreams)
            {
                var upstream = new TcpClient(address.AddressFamily) { NoDelay = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(UpstreamConnectTimeout);
                try
                {
                    await upstream.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
                    return upstream;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    upstream.Dispose();
                    lastError = $"{address}: {ex.Message}";
                }
            }
            throw new UnblockException($"No upstream for {Config.Domain} accepted the connection ({lastError}).");
        }

        // Reads exactly one TLS record (the ClientHello) and sends it as split records.
        // Anything that isn't a TLS handshake is forwarded untouched.
        private async Task ForwardClientHelloAsync(NetworkStream client, NetworkStream server, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ClientHelloTimeout);

            var header = new byte[TlsFragmenter.RecordHeaderLength];
            int read = await ReadAtLeastAsync(client, header, header.Length, timeout.Token).ConfigureAwait(false);
            var total = TlsFragmenter.HandshakeRecordLength(header.AsSpan(0, read));
            if (total is null || total > MaxClientHelloBytes)
            {
                await server.WriteAsync(header.AsMemory(0, read), ct).ConfigureAwait(false);
                return;
            }

            var record = new byte[total.Value];
            header.CopyTo(record, 0);
            int body = await ReadAtLeastAsync(client, record.AsMemory(header.Length), total.Value - header.Length, timeout.Token).ConfigureAwait(false);
            var available = record.AsMemory(0, header.Length + body);

            foreach (var part in TlsFragmenter.Split(available.Span, Config.Domain))
            {
                await server.WriteAsync(part, ct).ConfigureAwait(false);
                await server.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        private static async Task<int> ReadAtLeastAsync(NetworkStream stream, Memory<byte> buffer, int count, CancellationToken ct)
            => await stream.ReadAtLeastAsync(buffer[..count], count, throwOnEndOfStream: false, ct).ConfigureAwait(false);

        private static async Task PumpAsync(NetworkStream from, Socket to, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                int n;
                while ((n = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    await to.SendAsync(buffer.AsMemory(0, n), SocketFlags.None, ct).ConfigureAwait(false);
            }
            finally
            {
                // Propagate the half-close so request/response protocols finish cleanly.
                try { to.Shutdown(SocketShutdown.Send); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            Stopping.Cancel();
            foreach (var listener in _listeners) listener.Stop();
            _listeners.Clear();
            // Stopping is deliberately not disposed: in-flight connections may still observe its token.
        }
    }
}
