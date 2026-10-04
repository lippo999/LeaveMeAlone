using System.Net;
using LeaveMeAlone.Services.Unblock;
using Xunit;

namespace LeaveMeAlone.Tests.Unblock;

public class DomainNameTests
{
    [Theory]
    [InlineData("store.steampowered.com", "store.steampowered.com")]
    [InlineData("  Store.SteamPowered.COM ", "store.steampowered.com")]
    [InlineData("https://store.steampowered.com/app/570/", "store.steampowered.com")]
    [InlineData("steamcommunity.com.", "steamcommunity.com")]
    [InlineData("bücher.de", "xn--bcher-kva.de")]
    public void TryNormalize_AcceptsAndCanonicalizes(string input, string expected)
    {
        Assert.True(DomainName.TryNormalize(input, out var domain));
        Assert.Equal(expected, domain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("localhost")]
    [InlineData("104.83.198.104")]
    [InlineData("not a domain")]
    public void TryNormalize_RejectsNonDomains(string? input)
    {
        Assert.False(DomainName.TryNormalize(input, out _));
    }

    [Fact]
    public void Normalize_ThrowsUnblockException_OnInvalidInput()
    {
        Assert.Throws<UnblockException>(() => DomainName.Normalize("nope"));
    }
}

public class AddressClassifierTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("10.10.34.35")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.1")]
    [InlineData("169.254.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void IsBogon_True_ForAddressesNoPublicServerCanHave(string address)
    {
        Assert.True(AddressClassifier.IsBogon(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("104.83.198.104")]
    [InlineData("172.32.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700::1111")]
    public void IsBogon_False_ForPublicAddresses(string address)
    {
        Assert.False(AddressClassifier.IsBogon(IPAddress.Parse(address)));
    }
}

public class DnsWireTests
{
    [Fact]
    public void BuildQuery_EncodesHeaderAndLabels()
    {
        var query = DnsWire.BuildQuery(0xBEEF, "a.bc");

        Assert.Equal(new byte[] { 0xBE, 0xEF, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }, query[..12]);
        Assert.Equal(new byte[] { 1, (byte)'a', 2, (byte)'b', (byte)'c', 0, 0, 1, 0, 1 }, query[12..]);
    }

    [Fact]
    public void BuildQuery_RejectsEmptyLabel()
    {
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery(1, "a..b"));
    }

    [Fact]
    public void ParseAddresses_ReadsARecords_SkippingCnameAndCompressedNames()
    {
        var response = Response(0x1234, rcode: 0,
            Answer(type: 5, data: new byte[] { 3, (byte)'c', (byte)'d', (byte)'n', 0xC0, 0x0C }), // CNAME
            Answer(type: 1, data: new byte[] { 104, 83, 198, 104 }),
            Answer(type: 1, data: new byte[] { 23, 1, 2, 3 }));

        var addresses = DnsWire.ParseAddresses(response, 0x1234);

        Assert.Equal(new[] { IPAddress.Parse("104.83.198.104"), IPAddress.Parse("23.1.2.3") }, addresses);
    }

    [Fact]
    public void ParseAddresses_NxDomain_ReturnsEmpty()
    {
        Assert.Empty(DnsWire.ParseAddresses(Response(7, rcode: 3), 7));
    }

    [Fact]
    public void ParseAddresses_ServerFailure_Throws()
    {
        Assert.Throws<FormatException>(() => DnsWire.ParseAddresses(Response(7, rcode: 2), 7));
    }

    [Fact]
    public void ParseAddresses_WrongId_Throws()
    {
        Assert.Throws<FormatException>(() => DnsWire.ParseAddresses(Response(7, rcode: 0), 8));
    }

    [Fact]
    public void ParseAddresses_Truncated_Throws()
    {
        var response = Response(7, rcode: 0, Answer(type: 1, data: new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<FormatException>(() => DnsWire.ParseAddresses(response[..^2], 7));
    }

    private static byte[] Response(ushort id, int rcode, params byte[][] answers)
    {
        var question = DnsWire.BuildQuery(id, "x.example")[12..];
        var header = new byte[]
        {
            (byte)(id >> 8), (byte)id, 0x81, (byte)(0x80 | rcode), 0, 1, 0, (byte)answers.Length, 0, 0, 0, 0,
        };
        return header.Concat(question).Concat(answers.SelectMany(a => a)).ToArray();
    }

    // Owner name is a compression pointer to the question name at offset 12.
    private static byte[] Answer(ushort type, byte[] data) => new byte[]
        {
            0xC0, 0x0C, (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 60, (byte)(data.Length >> 8), (byte)data.Length,
        }.Concat(data).ToArray();
}

public class DohJsonTests
{
    [Fact]
    public void ParseJson_ReturnsOnlyARecords()
    {
        const string json = """
            {"Status":0,"Answer":[
              {"name":"store.steampowered.com","type":5,"data":"store.steampowered.com.edgekey.net."},
              {"name":"e.akamaiedge.net","type":1,"TTL":20,"data":"104.83.198.104"},
              {"name":"e.akamaiedge.net","type":1,"TTL":20,"data":"104.83.198.105"}]}
            """;

        Assert.Equal(
            new[] { IPAddress.Parse("104.83.198.104"), IPAddress.Parse("104.83.198.105") },
            DohResolver.ParseJson(json));
    }

    [Fact]
    public void ParseJson_NoAnswerSection_ReturnsEmpty()
    {
        Assert.Empty(DohResolver.ParseJson("""{"Status":3}"""));
    }
}
