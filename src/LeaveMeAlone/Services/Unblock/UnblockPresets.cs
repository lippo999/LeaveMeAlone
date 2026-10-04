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
}
