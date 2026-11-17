namespace LeaveMeAlone.Services.Unblock;

/// <param name="Name">Short title shown on the Quick screen.</param>
/// <param name="Description">One line telling the user what the switch fixes.</param>
public sealed record UnblockPreset(string Name, string Description, IReadOnlyList<string> Domains);

/// <summary>
/// Domain sets for the "quick unblock" button. Each domain is still diagnosed first; only the ones
/// actually blocked on the current network get unblocked, so listing extra domains is harmless.
/// hosts has no wildcards, hence the explicit sub-domains. A domain must belong to one preset only:
/// switching a preset off removes every domain it lists.
/// </summary>
public static class UnblockPresets
{
    /// <summary>
    /// Steam web (store, community, login, chat, workshop). Collected from the hosts the live store and
    /// community pages reference, checked against a Vietnamese ISP on 2026-11-17: every one returned
    /// 127.0.0.1 from the ISP resolver. Static assets are served from three interchangeable CDN
    /// families (akamai / fastly / cloudflare) and Steam switches between them, so all three are listed.
    /// </summary>
    public static readonly UnblockPreset Steam = new("Steam store & community",
        "Store pages, login, profiles, chat, workshop, images and videos.",
        new[]
        {
            "store.steampowered.com",
            "steamcommunity.com",
            "api.steampowered.com",
            "help.steampowered.com",
            "login.steampowered.com",
            "checkout.steampowered.com",
            "partner.steampowered.com",
            "media.steampowered.com",
            "partner.steamgames.com",
            "s.team",
            "steam-chat.com",
            "steam.tv",
            "video.steamstatic.com",
        }
        .Concat(StaticAssetHosts())
        .Concat(new[]
        {
            "steamcdn-a.akamaihd.net",
            "steamstore-a.akamaihd.net",
            "steamcommunity-a.akamaihd.net",
            "steamuserimages-a.akamaihd.net",
            "steamvideo-a.akamaihd.net",
            "steambroadcast.akamaized.net",
            "cdn.steamusercontent.com",
            "images.steamusercontent.com",
            "www.valvesoftware.com",
            "steamsupport.valvesoftware.com",
        })
        .ToArray());

    /// <summary>
    /// Steam download (content) servers, taken from Steam's own content_log for a Vietnamese ISP:
    /// the nearby cache regions plus the steampipe CDN front-ends, and the client self-update hosts.
    /// The ISP filters all of *.steamcontent.com; far regions (syd1, lax1, fra1, …) are omitted
    /// because Steam never picks them.
    /// </summary>
    public static readonly UnblockPreset SteamDownloads = new("Steam downloads",
        "Game downloads, updates and Steam client updates stuck at 0 B/s (content servers in HK, SG and JP).",
        new[]
        {
            "google2.cdn.steampipe.steamcontent.com",
            "fastly.cdn.steampipe.steamcontent.com",
            "alibaba.cdn.steampipe.steamcontent.com",
            "edgenext.cdn.steampipe.steamcontent.com",
            "cdn.steamstatic.com",
            "client-update.steamstatic.com",
            "client-update.akamai.steamstatic.com",
            "client-update.fastly.steamstatic.com",
        }
        .Concat(CacheHosts("hkg1", 12))
        .Concat(CacheHosts("sgp1", 10))
        .Concat(new[] { "cache2-tyo3.steamcontent.com", "cache10-tyo3.steamcontent.com" })
        .ToArray());

    /// <summary>Every preset, in the order the Quick screen shows them.</summary>
    public static readonly IReadOnlyList<UnblockPreset> All = new[] { Steam, SteamDownloads };

    private static readonly string[] StaticAssetKinds = { "store", "community", "shared", "cdn", "avatars", "clan", "video" };
    private static readonly string[] StaticAssetCdns = { "akamai", "fastly", "cloudflare" };

    /// <summary><c>{store,community,…}.{akamai,fastly,cloudflare}.steamstatic.com</c></summary>
    private static IEnumerable<string> StaticAssetHosts() =>
        from cdn in StaticAssetCdns
        from kind in StaticAssetKinds
        select $"{kind}.{cdn}.steamstatic.com";

    private static IEnumerable<string> CacheHosts(string region, int count) =>
        Enumerable.Range(1, count).Select(i => $"cache{i}-{region}.steamcontent.com");
}
