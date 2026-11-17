using LeaveMeAlone.Services.Unblock;
using Xunit;

namespace LeaveMeAlone.Tests.Unblock;

public class UnblockPresetsTests
{
    public static TheoryData<UnblockPreset> All => new() { UnblockPresets.Steam, UnblockPresets.SteamDownloads };

    [Theory]
    [MemberData(nameof(All))]
    public void Preset_domains_are_already_normalized_and_unique(UnblockPreset preset)
    {
        Assert.NotEmpty(preset.Domains);
        Assert.All(preset.Domains, d => Assert.Equal(d, DomainName.Normalize(d)));
        Assert.Equal(preset.Domains.Count, preset.Domains.Distinct().Count());
    }

    [Fact]
    public void All_lists_every_preset_and_no_domain_is_shared_between_presets()
    {
        Assert.Equal(new[] { UnblockPresets.Steam, UnblockPresets.SteamDownloads }, UnblockPresets.All);

        var all = UnblockPresets.All.SelectMany(p => p.Domains).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void Steam_covers_every_static_asset_cdn_the_store_page_uses()
    {
        var domains = UnblockPresets.Steam.Domains;

        foreach (var cdn in new[] { "akamai", "fastly", "cloudflare" })
        foreach (var kind in new[] { "store", "community", "shared", "cdn", "avatars", "clan", "video" })
            Assert.Contains($"{kind}.{cdn}.steamstatic.com", domains);
        Assert.Contains("steam-chat.com", domains);
        Assert.Contains("cdn.steamusercontent.com", domains);
    }

    [Fact]
    public void SteamDownloads_includes_client_update_hosts()
    {
        var domains = UnblockPresets.SteamDownloads.Domains;

        Assert.Contains("client-update.steamstatic.com", domains);
        Assert.Contains("client-update.fastly.steamstatic.com", domains);
        Assert.Contains("client-update.akamai.steamstatic.com", domains);
    }

    [Fact]
    public void SteamDownloads_covers_every_nearby_cache_server()
    {
        var domains = UnblockPresets.SteamDownloads.Domains;

        Assert.Contains("cache1-hkg1.steamcontent.com", domains);
        Assert.Contains("cache12-hkg1.steamcontent.com", domains);
        Assert.Contains("cache10-sgp1.steamcontent.com", domains);
        Assert.Contains("google2.cdn.steampipe.steamcontent.com", domains);
        Assert.DoesNotContain(domains, d => d.Contains("cache13-hkg1") || d.Contains("cache11-sgp1"));
    }
}
