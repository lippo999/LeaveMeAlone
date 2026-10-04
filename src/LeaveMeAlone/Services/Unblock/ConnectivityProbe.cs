using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace LeaveMeAlone.Services.Unblock;

public interface IConnectivityProbe
{
    /// <summary>TCP connect time to <paramref name="address"/>:443, or null if it couldn't connect.</summary>
    Task<TimeSpan?> TcpConnectAsync(IPAddress address, CancellationToken ct = default);

    /// <summary>TLS handshake to <paramref name="address"/>:443 with SNI = <paramref name="host"/>.</summary>
    Task<TlsProbeResult> TlsHandshakeAsync(IPAddress address, string host, bool fragmentClientHello, CancellationToken ct = default);
}

public sealed class ConnectivityProbe : IConnectivityProbe
{
    private const int HttpsPort = 443;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(6);

    public async Task<TimeSpan?> TcpConnectAsync(IPAddress address, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var socket = await ConnectAsync(address, ct).ConfigureAwait(false);
            return watch.Elapsed;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<TlsProbeResult> TlsHandshakeAsync(IPAddress address, string host, bool fragmentClientHello, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        Socket socket;
        try
        {
            socket = await ConnectAsync(address, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new TlsProbeResult(TlsOutcome.ConnectFailed, watch.Elapsed, ex.Message);
        }

        using (socket)
        {
            Stream stream = new NetworkStream(socket, ownsSocket: false);
            if (fragmentClientHello) stream = new ClientHelloFragmentingStream(stream, host);

            var certificateErrors = SslPolicyErrors.None;
            using var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, errors) =>
            {
                certificateErrors = errors;
                return true; // inspect, don't reject: a mismatch tells us the IP isn't really this domain
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HandshakeTimeout);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, timeout.Token).ConfigureAwait(false);

                var outcome = certificateErrors == SslPolicyErrors.None ? TlsOutcome.Ok : TlsOutcome.CertificateMismatch;
                return new TlsProbeResult(outcome, watch.Elapsed, outcome == TlsOutcome.Ok ? null : certificateErrors.ToString());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new TlsProbeResult(TlsOutcome.Timeout, watch.Elapsed, "TLS handshake timed out.");
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or SocketException)
            {
                return new TlsProbeResult(TlsOutcome.Reset, watch.Elapsed, ex.GetBaseException().Message);
            }
        }
    }

    private static async Task<Socket> ConnectAsync(IPAddress address, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, HttpsPort), timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Stream wrapper that sends the first write (the ClientHello) as split TLS records via
/// <see cref="TlsFragmenter"/>, then passes everything else through untouched.
/// </summary>
public sealed class ClientHelloFragmentingStream : Stream
{
    private readonly Stream _inner;
    private readonly string _host;
    private bool _helloSent;

    public ClientHelloFragmentingStream(Stream inner, string host)
    {
        _inner = inner;
        _host = host;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (_helloSent)
        {
            await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
            return;
        }

        _helloSent = true;
        foreach (var part in TlsFragmenter.Split(buffer.Span, _host))
        {
            await _inner.WriteAsync(part, ct).ConfigureAwait(false);
            await _inner.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
