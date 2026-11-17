using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeaveMeAlone.Services.Update;

namespace LeaveMeAlone.Tests.Update;

/// <summary>In-memory <see cref="IUpdateHttp"/>: serves canned strings and file bodies by URL.</summary>
internal sealed class FakeUpdateHttp : IUpdateHttp
{
    public Dictionary<Uri, string> Strings { get; } = new();
    public Dictionary<Uri, byte[]> Files { get; } = new();
    public Exception? Failure { get; set; }
    public List<Uri> Requested { get; } = new();

    public Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        Requested.Add(uri);
        if (Failure != null) throw Failure;
        return Strings.TryGetValue(uri, out var s) ? Task.FromResult(s) : throw new HttpRequestException($"404 {uri}");
    }

    public async Task DownloadAsync(Uri uri, string destinationPath, CancellationToken cancellationToken = default)
    {
        Requested.Add(uri);
        if (Failure != null) throw Failure;
        if (!Files.TryGetValue(uri, out var bytes)) throw new HttpRequestException($"404 {uri}");
        await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);
    }

    /// <summary>Publishes <paramref name="exe"/> + a matching (or deliberately wrong) checksum for <paramref name="update"/>.</summary>
    public void Serve(AvailableUpdate update, byte[] exe, string? checksumOverride = null)
    {
        Files[update.ExeUrl] = exe;
        var hash = checksumOverride ?? Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant();
        Strings[update.ChecksumUrl] = $"{hash}  {UpdateSource.ExeAssetName}\n";
    }
}

/// <summary>Builds GitHub "latest release" API responses.</summary>
internal static class Releases
{
    private const string Base = "https://github.com/lippo999/LeaveMeAlone/releases";

    public static Uri ExeUrl(string tag) => new($"{Base}/download/{tag}/{UpdateSource.ExeAssetName}");
    public static Uri ShaUrl(string tag) => new($"{Base}/download/{tag}/{UpdateSource.ChecksumAssetName}");

    public static AvailableUpdate Update(string tag = "v0.3.0") =>
        new(Version.Parse(tag.TrimStart('v')), tag, ExeUrl(tag), ShaUrl(tag), new Uri($"{Base}/tag/{tag}"));

    public static string Json(string tag, bool prerelease = false, bool draft = false,
        bool includeExe = true, bool includeSha = true)
    {
        var assets = new List<object>();
        if (includeExe) assets.Add(new { name = UpdateSource.ExeAssetName, browser_download_url = ExeUrl(tag).ToString() });
        if (includeSha) assets.Add(new { name = UpdateSource.ChecksumAssetName, browser_download_url = ShaUrl(tag).ToString() });
        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = $"{Base}/tag/{tag}",
            draft,
            prerelease,
            assets,
        });
    }

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
