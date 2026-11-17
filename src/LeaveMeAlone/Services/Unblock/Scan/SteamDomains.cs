using System.Text.RegularExpressions;

namespace LeaveMeAlone.Services.Unblock.Scan;

/// <summary>
/// The Steam allowlist. The scan only ever looks at domains that pass <see cref="IsSteam"/>, so data it
/// reads from shared places (the Windows DNS cache holds every site the user visits) can never reroute
/// anything that isn't Steam. Other domains stay a manual decision on the Custom tab.
/// </summary>
public static class SteamDomains
{
    /// <summary>Registrable domains owned by Valve; the domain itself and every sub-domain match.</summary>
    private static readonly string[] Owned =
    {
        "steampowered.com",
        "steamcommunity.com",
        "steamstatic.com",
        "steamcontent.com",
        "steamusercontent.com",
        "steamgames.com",
        "steamserver.net",
        "steam-chat.com",
        "steam-api.com",
        "steam.tv",
        "s.team",
        "valvesoftware.com",
    };

    /// <summary>Shared CDN zones where only Steam's own hosts (<c>steam*.akamaihd.net</c>) may match.</summary>
    private static readonly string[] SharedCdnZones = { "akamaihd.net", "akamaized.net" };

    // Host-like tokens: labels of letters/digits/hyphens joined by dots, ending in an alphabetic TLD.
    private static readonly Regex HostToken = new(
        @"(?<![A-Za-z0-9.-])(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,24}(?![A-Za-z0-9-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsSteam(string domain)
    {
        if (!DomainName.TryNormalize(domain, out var host)) return false;
        if (Owned.Any(zone => host == zone || host.EndsWith("." + zone, StringComparison.Ordinal))) return true;

        // Exactly one label in front of the shared zone, and that label is Steam's (steamcdn-a, steamuserimages-a, …).
        return SharedCdnZones.Any(zone =>
            host.EndsWith("." + zone, StringComparison.Ordinal)
            && host.IndexOf('.') == host.Length - zone.Length - 1
            && host.StartsWith("steam", StringComparison.Ordinal));
    }

    /// <summary>Every distinct Steam host mentioned in free text (log lines, HTML, JSON with escaped slashes).</summary>
    public static IReadOnlyList<string> ExtractHosts(string text) => HostToken.Matches(text)
        .Select(m => m.Value)
        .Where(IsSteam)
        .Select(DomainName.Normalize)
        .Distinct()
        .ToList();
}
