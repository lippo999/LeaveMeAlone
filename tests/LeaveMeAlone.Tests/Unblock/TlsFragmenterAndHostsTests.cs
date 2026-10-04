using System.Text;
using LeaveMeAlone.Services.Unblock;
using Xunit;

namespace LeaveMeAlone.Tests.Unblock;

public class TlsFragmenterTests
{
    private const string Host = "store.steampowered.com";

    [Fact]
    public void Split_CutsClientHelloIntoTwoRecordsThroughTheHostName()
    {
        var hello = FakeClientHello(Host);

        var parts = TlsFragmenter.Split(hello, Host);

        Assert.Equal(2, parts.Count);
        foreach (var part in parts)
        {
            Assert.Equal(0x16, part[0]);
            Assert.Equal(part.Length - 5, (part[3] << 8) | part[4]);
            Assert.False(Contains(part, Host), "no single record may carry the full host name");
        }
        // Reassembled payload is exactly the original handshake message.
        Assert.Equal(hello[5..], parts[0][5..].Concat(parts[1][5..]).ToArray());
        Assert.Equal(hello[1..3], parts[1][1..3]);
    }

    [Fact]
    public void Split_HostNotFound_CutsAfterFirstByte()
    {
        var hello = FakeClientHello("other.example");

        var parts = TlsFragmenter.Split(hello, Host);

        Assert.Equal(6, parts[0].Length);
        Assert.Equal(hello[5..], parts[0][5..].Concat(parts[1][5..]).ToArray());
    }

    [Fact]
    public void Split_KeepsBytesAfterTheRecord()
    {
        var hello = FakeClientHello(Host).Concat(new byte[] { 0x17, 0x03, 0x03, 0, 1, 0xAA }).ToArray();

        var parts = TlsFragmenter.Split(hello, Host);

        Assert.Equal(new byte[] { 0x17, 0x03, 0x03, 0, 1, 0xAA }, parts[1][^6..]);
    }

    [Theory]
    [InlineData(new byte[] { 0x47, 0x45, 0x54, 0x20, 0x2F, 0x20 })]  // "GET / " — not TLS
    [InlineData(new byte[] { 0x16, 0x03, 0x01, 0x00, 0x40, 0x01 })]  // record longer than the data
    [InlineData(new byte[] { 0x16, 0x03 })]                          // shorter than a header
    public void Split_LeavesAnythingElseUntouched(byte[] data)
    {
        var parts = TlsFragmenter.Split(data, Host);
        Assert.Equal(data, Assert.Single(parts));
    }

    internal static byte[] FakeClientHello(string host)
    {
        var body = new byte[] { 0x01, 0x00, 0x00, 0x50 }
            .Concat(new byte[40])
            .Concat(Encoding.ASCII.GetBytes(host))
            .Concat(new byte[30])
            .ToArray();
        return new byte[] { 0x16, 0x03, 0x01, (byte)(body.Length >> 8), (byte)body.Length }.Concat(body).ToArray();
    }

    private static bool Contains(byte[] haystack, string needle)
        => haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(needle)) >= 0;
}

public class HostsBlockTests
{
    private static readonly HostsEntry[] Entries =
    {
        new("127.77.0.1", "store.steampowered.com"),
        new("104.83.197.9", "steamcommunity.com"),
    };

    [Theory]
    [InlineData("")]
    [InlineData("# Copyright (c) Microsoft\r\n127.0.0.1 localhost\r\n")]
    [InlineData("# no trailing newline\r\n10.0.0.5 nas.lan")]
    [InlineData("# unix endings\n10.0.0.5 nas.lan\n")]
    public void ApplyThenRemove_RestoresOriginalExactly(string original)
    {
        var applied = HostsBlock.Apply(original, Entries);

        Assert.Contains("127.77.0.1 store.steampowered.com", applied);
        Assert.Equal(original, HostsBlock.Remove(applied));
        Assert.Equal(original, HostsBlock.Apply(applied, Array.Empty<HostsEntry>()));
    }

    [Fact]
    public void Apply_ReplacesExistingBlockInsteadOfAddingASecond()
    {
        var once = HostsBlock.Apply("127.0.0.1 localhost\r\n", Entries);
        var twice = HostsBlock.Apply(once, Entries[..1]);

        Assert.Single(AllIndexes(twice, HostsBlock.BeginMarker));
        Assert.DoesNotContain("steamcommunity.com", twice);
    }

    [Fact]
    public void Apply_PreservesUserLinesAfterOurBlock()
    {
        var applied = HostsBlock.Apply("a 1.example\r\n", Entries) + "10.0.0.9 later.example\r\n";

        var updated = HostsBlock.Apply(applied, Entries[..1]);

        Assert.EndsWith("10.0.0.9 later.example\r\n", updated);
        Assert.Equal("a 1.example\r\n10.0.0.9 later.example\r\n", HostsBlock.Remove(updated));
    }

    [Fact]
    public void Apply_UsesFileNewlineStyle()
    {
        var applied = HostsBlock.Apply("a b.example\n", Entries);
        Assert.DoesNotContain("\r\n", applied);
    }

    [Theory]
    [InlineData("1.2.3.4 store.steampowered.com\r\n", true)]
    [InlineData("1.2.3.4   other.example   STORE.steampowered.com # alias\r\n", true)]
    [InlineData("# 1.2.3.4 store.steampowered.com\r\n", false)]
    [InlineData("1.2.3.4 store.steampowered.com.evil\r\n", false)]
    public void HasForeignEntry_DetectsUserEntries(string content, bool expected)
    {
        Assert.Equal(expected, HostsBlock.HasForeignEntry(content, "store.steampowered.com"));
    }

    [Fact]
    public void HasForeignEntry_IgnoresOurOwnBlock()
    {
        var applied = HostsBlock.Apply("", Entries);
        Assert.False(HostsBlock.HasForeignEntry(applied, "store.steampowered.com"));
    }

    private static IEnumerable<int> AllIndexes(string text, string value)
    {
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            yield return i;
    }
}
