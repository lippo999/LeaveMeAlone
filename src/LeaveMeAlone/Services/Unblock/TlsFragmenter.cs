using System.Text;

namespace LeaveMeAlone.Services.Unblock;

/// <summary>
/// Splits a TLS ClientHello record into two TLS records, cutting through the middle of the SNI
/// host name. TLS allows a handshake message to span several records (RFC 8446 §5.1), so the
/// server reassembles it transparently, but DPI that inspects one record at a time never sees the
/// full host name. Measured on a Vietnamese ISP: plain TCP segmentation is still reset (the DPI
/// reassembles TCP), while record splitting gets through.
/// </summary>
public static class TlsFragmenter
{
    public const int RecordHeaderLength = 5;
    public const byte HandshakeContentType = 0x16;

    /// <summary>Total length of the TLS record starting at <paramref name="header"/>, or null if it isn't a handshake record.</summary>
    public static int? HandshakeRecordLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < RecordHeaderLength || header[0] != HandshakeContentType || header[1] != 0x03) return null;
        return RecordHeaderLength + ((header[3] << 8) | header[4]);
    }

    /// <summary>
    /// Returns the bytes to send, as separate writes. Data that isn't one complete handshake record
    /// is returned unchanged as a single part.
    /// </summary>
    public static IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> data, string host)
    {
        var total = HandshakeRecordLength(data);
        if (total is null || data.Length < total || total <= RecordHeaderLength + 1)
            return new[] { data.ToArray() };

        var payload = data[RecordHeaderLength..total.Value];
        var trailing = data[total.Value..];
        int cut = CutPoint(payload, host);

        var first = Record(data[1], data[2], payload[..cut]);
        var second = Record(data[1], data[2], payload[cut..]);
        return trailing.IsEmpty
            ? new[] { first, second }
            : new[] { first, second.Concat(trailing.ToArray()).ToArray() };
    }

    // Middle of the host name when we can find it; otherwise right after the handshake type byte,
    // so the first record can't be parsed as a ClientHello at all.
    private static int CutPoint(ReadOnlySpan<byte> payload, string host)
    {
        var index = payload.IndexOf(Encoding.ASCII.GetBytes(host));
        if (index < 0) return 1;
        return Math.Clamp(index + host.Length / 2, 1, payload.Length - 1);
    }

    private static byte[] Record(byte versionMajor, byte versionMinor, ReadOnlySpan<byte> payload)
    {
        var record = new byte[RecordHeaderLength + payload.Length];
        record[0] = HandshakeContentType;
        record[1] = versionMajor;
        record[2] = versionMinor;
        record[3] = (byte)(payload.Length >> 8);
        record[4] = (byte)payload.Length;
        payload.CopyTo(record.AsSpan(RecordHeaderLength));
        return record;
    }
}
