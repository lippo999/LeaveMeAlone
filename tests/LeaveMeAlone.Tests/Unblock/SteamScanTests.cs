using System.IO;
using LeaveMeAlone.Services;
using LeaveMeAlone.Services.Unblock;
using LeaveMeAlone.Services.Unblock.Scan;
using Xunit;

namespace LeaveMeAlone.Tests.Unblock;

public class SteamDomainsTests
{
    [Theory]
    [InlineData("store.steampowered.com")]
    [InlineData("steamcommunity.com")]
    [InlineData("shared.fastly.steamstatic.com")]
    [InlineData("cache3-hkg1.steamcontent.com")]
    [InlineData("cdn.steamusercontent.com")]
    [InlineData("cmp1-sgp1.steamserver.net")]
    [InlineData("s.team")]
    [InlineData("steam-chat.com")]
    [InlineData("steamcdn-a.akamaihd.net")]
    [InlineData("steambroadcast.akamaized.net")]
    [InlineData("STORE.SteamPowered.com")]
    public void IsSteam_accepts_steam_hosts(string host) => Assert.True(SteamDomains.IsSteam(host));

    [Theory]
    [InlineData("google.com")]
    [InlineData("api.anthropic.com")]
    [InlineData("evilsteampowered.com")]           // not a sub-domain, just a suffix
    [InlineData("steampowered.com.evil.net")]
    [InlineData("a1234.akamaihd.net")]             // shared CDN, not Steam's host
    [InlineData("steam.x.akamaihd.net")]           // only one label allowed in front of the shared zone
    [InlineData("netflix.akamaized.net")]
    [InlineData("localhost")]
    [InlineData("")]
    public void IsSteam_rejects_everything_else(string host) => Assert.False(SteamDomains.IsSteam(host));

    [Fact]
    public void ExtractHosts_finds_steam_hosts_in_logs_html_and_escaped_json_only()
    {
        const string text =
            "[2026-11-17] Download from cache8-hkg1.steamcontent.com:443 failed\n" +
            "<script src=\"https://shared.fastly.steamstatic.com/a.js\"></script> https://www.youtube.com/x\n" +
            "{\"u\":\"https:\\/\\/video.akamai.steamstatic.com\\/v.mp4\"} cache8-hkg1.steamcontent.com";

        var hosts = SteamDomains.ExtractHosts(text);

        Assert.Equal(
            new[] { "cache8-hkg1.steamcontent.com", "shared.fastly.steamstatic.com", "video.akamai.steamstatic.com" },
            hosts.OrderBy(h => h));
    }
}

public class DnsCacheSourceTests
{
    [Fact]
    public void ParsePoisoned_keeps_only_steam_names_with_bogus_addresses()
    {
        const string output =
            "shared.fastly.steamstatic.com\t127.0.0.1\r\n" +
            "steam.tv\t0.0.0.0\r\n" +
            "store.steampowered.com\t42.119.186.138\r\n" + // real address
            "ads.example.com\t0.0.0.0\r\n" +              // poisoned but not Steam: must be ignored
            "steam.tv\t::1\r\n" +
            "garbage line\r\n";

        Assert.Equal(new[] { "shared.fastly.steamstatic.com", "steam.tv" }, DnsCacheSource.ParsePoisoned(output));
    }

    [Fact]
    public async Task FindAsync_runs_powershell_and_throws_on_failure()
    {
        var ok = new FakeProcessRunner(new ProcessResult(0, "s.team\t127.0.0.1\n", ""));
        Assert.Equal(new[] { "s.team" }, await new DnsCacheSource(ok).FindAsync());
        Assert.Contains("Get-DnsClientCache", ok.LastArguments);

        var denied = new FakeProcessRunner(new ProcessResult(1, "", "Access denied"));
        var ex = await Assert.ThrowsAsync<UnblockException>(() => new DnsCacheSource(denied).FindAsync());
        Assert.Contains("Access denied", ex.Message);
    }
}

public sealed class SteamLogSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lma-steamlogs-" + Guid.NewGuid().ToString("N"));

    public SteamLogSourceTests() => Directory.CreateDirectory(Path.Combine(_dir, "logs"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task FindAsync_reads_steam_hosts_from_every_log_even_while_steam_has_it_open()
    {
        var content = Path.Combine(_dir, "logs", "content_log.txt");
        File.WriteAllText(content, "using cache2-sgp1.steamcontent.com and www.google.com\n");
        File.WriteAllText(Path.Combine(_dir, "logs", "cef_log.txt"), "GET https://store.fastly.steamstatic.com/x.css\n");
        File.WriteAllText(Path.Combine(_dir, "logs", "ignored.log"), "not-scanned.steampowered.com\n");

        using var heldOpen = new FileStream(content, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var hosts = await new SteamLogSource(() => _dir).FindAsync();

        Assert.Equal(new[] { "cache2-sgp1.steamcontent.com", "store.fastly.steamstatic.com" }, hosts.OrderBy(h => h));
    }

    [Fact]
    public async Task FindAsync_throws_when_steam_is_not_installed()
    {
        await Assert.ThrowsAsync<UnblockException>(() => new SteamLogSource(() => null).FindAsync());
    }
}

public class SteamScannerTests
{
    private readonly FakeDiagnoser _diagnoser = new FakeDiagnoser()
        .Returns("store.steampowered.com", BlockKind.Dns, "42.119.186.138")
        .Returns("shared.fastly.steamstatic.com", BlockKind.DnsAndSni, "199.232.215.52")
        .Returns("steam.tv", BlockKind.None, "104.83.197.9")
        .Returns("steamstats.valve-other.steampowered.com", BlockKind.Other)
        .Returns("nowork.steamstatic.com", BlockKind.Dns);           // blocked but no usable IP
    private readonly MemoryUnblockStore _store = new();

    private UnblockManager NewManager() => new(_store, new FakeHostsFile(), _diagnoser, new FakeRelayHost());

    [Fact]
    public async Task Scan_returns_only_fixable_steam_domains_with_their_sources()
    {
        var scanner = new SteamScanner(NewManager(), new IDomainSource[]
        {
            new FakeSource("DNS cache", "shared.fastly.steamstatic.com", "google.com", "steam.tv"),
            new FakeSource("Steam logs", "shared.fastly.steamstatic.com", "steamstats.valve-other.steampowered.com",
                "nowork.steamstatic.com"),
        }, knownDomains: new[] { "store.steampowered.com" });

        var result = await scanner.ScanAsync();

        Assert.Equal(new[] { "shared.fastly.steamstatic.com", "store.steampowered.com" }, result.Blocked.Select(f => f.Domain));
        Assert.Equal(new[] { "DNS cache", "Steam logs" }, result.Blocked[0].Sources);
        Assert.Equal(new[] { SteamScanner.PresetSource }, result.Blocked[1].Sources);
        Assert.Equal(5, result.Checked); // google.com never reaches the diagnoser
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task Scan_skips_domains_already_rerouted()
    {
        var manager = NewManager();
        await manager.UnblockAsync("store.steampowered.com");

        var result = await new SteamScanner(manager, Array.Empty<IDomainSource>(),
            knownDomains: new[] { "store.steampowered.com" }).ScanAsync();

        Assert.Empty(result.Blocked);
        Assert.Equal(0, result.Checked);
    }

    [Fact]
    public async Task Scan_reports_broken_sources_and_undiagnosable_domains_but_keeps_going()
    {
        var scanner = new SteamScanner(NewManager(), new IDomainSource[]
        {
            new FakeSource("Steam logs") { Failure = new UnblockException("no Steam") },
            new FakeSource("DNS cache", "unknown.steampowered.com"), // FakeDiagnoser throws for unknown domains
        }, knownDomains: new[] { "store.steampowered.com" });

        var result = await scanner.ScanAsync();

        Assert.Equal(new[] { "store.steampowered.com" }, result.Blocked.Select(f => f.Domain));
        Assert.Contains(result.Problems, p => p.StartsWith("Steam logs: no Steam"));
        Assert.Contains(result.Problems, p => p.StartsWith("unknown.steampowered.com:"));
    }

    [Fact]
    public void Default_known_domains_are_the_presets_and_all_steam()
    {
        Assert.All(UnblockPresets.All.SelectMany(p => p.Domains), d => Assert.True(SteamDomains.IsSteam(d), d));
    }
}

internal sealed class FakeSource : IDomainSource
{
    private readonly string[] _domains;

    public FakeSource(string name, params string[] domains)
    {
        Name = name;
        _domains = domains;
    }

    public string Name { get; }
    public Exception? Failure { get; init; }

    public Task<IReadOnlyList<string>> FindAsync(CancellationToken ct = default) =>
        Failure is null ? Task.FromResult<IReadOnlyList<string>>(_domains) : Task.FromException<IReadOnlyList<string>>(Failure);
}

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly ProcessResult _result;

    public FakeProcessRunner(ProcessResult result) => _result = result;

    public string LastArguments { get; private set; } = "";

    public Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct = default)
    {
        LastArguments = arguments;
        return Task.FromResult(_result);
    }
}
