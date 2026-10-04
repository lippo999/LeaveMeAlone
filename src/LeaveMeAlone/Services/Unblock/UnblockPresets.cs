namespace LeaveMeAlone.Services.Unblock;

public sealed record UnblockPreset(string Name, IReadOnlyList<string> Domains);

/// <summary>
/// Domain sets for the "quick unblock" button. Each domain is still diagnosed first; only the ones
/// actually blocked on the current network get unblocked, so listing extra domains is harmless.
/// hosts has no wildcards, hence the explicit sub-domains.
/// </summary>
public static class UnblockPresets
{
    public static readonly UnblockPreset Steam = new("Steam (store + community)", new[]
    {
        "store.steampowered.com",
        "steamcommunity.com",
        "api.steampowered.com",
        "help.steampowered.com",
        "login.steampowered.com",
        "checkout.steampowered.com",
        "store.akamai.steamstatic.com",
        "community.akamai.steamstatic.com",
        "shared.akamai.steamstatic.com",
        "cdn.akamai.steamstatic.com",
        "avatars.akamai.steamstatic.com",
    });

    /// <summary>
    /// Steam download (content) servers, taken from Steam's own content_log for a Vietnamese ISP:
    /// the nearby cache regions plus the steampipe CDN front-ends. The ISP filters all of
    /// *.steamcontent.com; far regions (syd1, lax1, fra1, …) are omitted because Steam never picks them.
    /// </summary>
    public static readonly UnblockPreset SteamDownloads = new("Steam downloads (content servers)",
        new[]
        {
            "google2.cdn.steampipe.steamcontent.com",
            "fastly.cdn.steampipe.steamcontent.com",
            "alibaba.cdn.steampipe.steamcontent.com",
            "edgenext.cdn.steampipe.steamcontent.com",
        }
        .Concat(CacheHosts("hkg1", 12))
        .Concat(CacheHosts("sgp1", 10))
        .Concat(new[] { "cache2-tyo3.steamcontent.com", "cache10-tyo3.steamcontent.com" })
        .ToArray());

    private static IEnumerable<string> CacheHosts(string region, int count) =>
        Enumerable.Range(1, count).Select(i => $"cache{i}-{region}.steamcontent.com");
}
