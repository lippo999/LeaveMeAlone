using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace LeaveMeAlone.Services.Unblock;

/// <summary>
/// Minimal DNS wire format (RFC 1035) for A/AAAA. Used to ask the system's configured DNS servers
/// directly — <see cref="Dns.GetHostAddressesAsync(string)"/> would read the hosts file and return
/// our own pins instead of what the ISP answers.
/// </summary>
public static class DnsWire
{
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;
    private const ushort ClassIn = 1;

    public static byte[] BuildQuery(ushort id, string domain, ushort type = TypeA)
    {
        var buffer = new List<byte>(32 + domain.Length);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // standard query, recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);      // QDCOUNT
        buffer.AddRange(header.ToArray());

        foreach (var label in domain.TrimEnd('.').Split('.'))
        {
            if (label.Length is 0 or > 63) throw new ArgumentException($"Invalid DNS label in '{domain}'.");
            buffer.Add((byte)label.Length);
            buffer.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        buffer.Add(0);
        buffer.AddRange(new byte[] { (byte)(type >> 8), (byte)type, 0, (byte)ClassIn });
        return buffer.ToArray();
    }

    /// <summary>Extracts the A and AAAA records from a response. Throws <see cref="FormatException"/> on malformed data.</summary>
    public static IReadOnlyList<IPAddress> ParseAddresses(ReadOnlySpan<byte> response, ushort expectedId)
    {
        if (response.Length < 12) throw new FormatException("DNS response too short.");
        if (BinaryPrimitives.ReadUInt16BigEndian(response) != expectedId) throw new FormatException("DNS response ID mismatch.");

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        if ((flags & 0x8000) == 0) throw new FormatException("Not a DNS response.");
        var rcode = flags & 0x000F;
        if (rcode == 3) return Array.Empty<IPAddress>(); // NXDOMAIN
        if (rcode != 0) throw new FormatException($"DNS server returned error code {rcode}.");

        int questions = BinaryPrimitives.ReadUInt16BigEndian(response[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);
        int offset = 12;

        for (int i = 0; i < questions; i++)
        {
            SkipName(response, ref offset);
            offset += 4; // QTYPE + QCLASS
        }

        var result = new List<IPAddress>();
        for (int i = 0; i < answers; i++)
        {
            SkipName(response, ref offset);
            if (offset + 10 > response.Length) throw new FormatException("Truncated DNS answer.");
            var type = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
            var cls = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 2)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 8)..]);
            offset += 10;
            if (offset + length > response.Length) throw new FormatException("Truncated DNS record data.");

            if (cls == ClassIn && ((type == TypeA && length == 4) || (type == TypeAaaa && length == 16)))
                result.Add(new IPAddress(response.Slice(offset, length)));
            offset += length;
        }
        return result;
    }

    private static void SkipName(ReadOnlySpan<byte> data, ref int offset)
    {
        while (true)
        {
            if (offset >= data.Length) throw new FormatException("Truncated DNS name.");
            int len = data[offset];
            if (len == 0) { offset += 1; return; }
            if ((len & 0xC0) == 0xC0) { offset += 2; return; } // compression pointer ends the name
            offset += len + 1;
        }
    }
}

/// <summary>Addresses that can never be a public server — a DNS answer containing one is forged.</summary>
public static class AddressClassifier
{
    public static bool IsBogon(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6None.Equals(address)
                || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 unique-local
        }

        var b = address.GetAddressBytes();
        return b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // carrier-grade NAT
    }
}
