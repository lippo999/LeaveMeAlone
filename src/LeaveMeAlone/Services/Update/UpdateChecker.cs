using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeaveMeAlone.Services.Update;

/// <summary>Asks GitHub for the latest stable release and returns it when it is newer than this build.</summary>
public sealed class UpdateChecker
{
    private readonly IUpdateHttp _http;
    private readonly Version _current;

    public UpdateChecker(IUpdateHttp http, Version current)
    {
        _http = http;
        _current = current;
    }

    public Version CurrentVersion => _current;

    /// <returns>The newer release, or null when this build is up to date.</returns>
    /// <exception cref="UpdateException">GitHub could not be reached or answered something unexpected.</exception>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        string json;
        try
        {
            json = await _http.GetStringAsync(UpdateSource.LatestReleaseApi, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new UpdateException($"Couldn't check for updates: {ex.Message}", ex);
        }
        return ReleaseParser.Parse(json, _current);
    }
}

/// <summary>Reads GitHub's "latest release" JSON. Pure, so every edge case is unit-tested.</summary>
public static class ReleaseParser
{
    public static AvailableUpdate? Parse(string json, Version current)
    {
        Release release;
        try
        {
            release = JsonSerializer.Deserialize<Release>(json)
                      ?? throw new UpdateException("GitHub returned an empty release.");
        }
        catch (JsonException ex)
        {
            throw new UpdateException("GitHub returned an unexpected release format.", ex);
        }
        if (release.tag_name is null)
            throw new UpdateException("GitHub returned a release without a version tag.");

        if (release.draft || release.prerelease) return null;
        if (ParseTag(release.tag_name) is not { } version) return null;
        if (Normalize(version) <= Normalize(current)) return null;

        var exe = FindAsset(release, UpdateSource.ExeAssetName);
        var checksum = FindAsset(release, UpdateSource.ChecksumAssetName);
        if (exe is null || checksum is null) return null;

        var page = Uri.TryCreate(release.html_url, UriKind.Absolute, out var p)
            ? p
            : new Uri($"https://github.com/{UpdateSource.Repository}/releases/tag/{release.tag_name}");
        return new AvailableUpdate(version, release.tag_name, exe, checksum, page);
    }

    /// <summary>"v0.3.0" / "0.3.0" → 0.3.0. Pre-release style tags ("v1.0-beta", "nightly") are not updates.</summary>
    private static Version? ParseTag(string tag)
    {
        var text = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        return Version.TryParse(text, out var version) ? version : null;
    }

    // Version treats a missing part as -1, so 0.2.0 < 0.2.0.0; compare with missing parts as zero instead.
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static Uri? FindAsset(Release release, string name)
    {
        var asset = release.assets?.FirstOrDefault(a => string.Equals(a.name, name, StringComparison.OrdinalIgnoreCase));
        return asset?.browser_download_url is { } url
               && Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;
    }

    // Property names mirror GitHub's JSON exactly (snake_case), so no naming policy is needed.
#pragma warning disable IDE1006
    private sealed record Release(string? tag_name, string? html_url, bool draft, bool prerelease, List<Asset>? assets);
    private sealed record Asset(string? name, string? browser_download_url);
#pragma warning restore IDE1006
}

/// <summary>Reads the "&lt;sha256&gt;  LeaveMeAlone.exe" file the CI publishes next to the exe.</summary>
public static partial class Checksum
{
    /// <returns>The lowercase 64-character hex digest.</returns>
    public static string Parse(string text)
    {
        var first = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!Sha256Hex().IsMatch(first))
            throw new UpdateException("The release's checksum file is not a valid SHA-256 hash.");
        return first.ToLowerInvariant();
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();
}
