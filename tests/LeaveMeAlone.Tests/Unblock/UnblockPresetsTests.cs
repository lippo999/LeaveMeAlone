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
