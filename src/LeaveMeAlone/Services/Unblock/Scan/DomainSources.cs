using System.IO;
using System.Net;
using System.Net.Http;

namespace LeaveMeAlone.Services.Unblock.Scan;

/// <summary>Somewhere the scan can learn which Steam hosts are in use. Results are filtered to Steam by the scanner too.</summary>
public interface IDomainSource
{
    /// <summary>Short label shown next to each finding ("DNS cache", "Steam logs", …).</summary>
    string Name { get; }

    /// <summary>Throws when the source can't be read; the scanner reports that and carries on with the others.</summary>
    Task<IReadOnlyList<string>> FindAsync(CancellationToken ct = default);
}

/// <summary>
/// Steam hosts the ISP's DNS just answered with a bogus address (127.0.0.1, 0.0.0.0, ::1 …), read from the
/// Windows DNS client cache. Catches whatever Steam tried to reach a moment ago, including hosts no preset
/// knows yet. Needs administrator rights (the app always has them when it can unblock anything).
/// </summary>
public sealed class DnsCacheSource : IDomainSource
{
    // One "name<TAB>address" line per A/AAAA record; Entry is the name that was looked up.
    private const string Script =
        "Get-DnsClientCache | Where-Object { $_.Type -eq 1 -or $_.Type -eq 28 } | " +
        "ForEach-Object { \"$($_.Entry)`t$($_.Data)\" }";

    private readonly IProcessRunner _runner;

    public DnsCacheSource(IProcessRunner runner) => _runner = runner;

    public string Name => "DNS cache";

    public async Task<IReadOnlyList<string>> FindAsync(CancellationToken ct = default)
    {
        var result = await _runner.RunAsync("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{Script.Replace("\"", "\\\"")}\"", ct)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new UnblockException($"couldn't read the DNS cache: {result.StdErr.Trim()}");
        return ParsePoisoned(result.StdOut);
    }

    /// <summary>Steam names that have at least one bogon address in <c>name&lt;TAB&gt;address</c> lines.</summary>
    public static IReadOnlyList<string> ParsePoisoned(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split('\t'))
        .Where(parts => parts.Length == 2
                        && IPAddress.TryParse(parts[1], out var address)
                        && AddressClassifier.IsBogon(address)
                        && SteamDomains.IsSteam(parts[0]))
        .Select(parts => DomainName.Normalize(parts[0]))
        .Distinct()
        .ToList();
}

/// <summary>
/// Steam hosts named in the Steam client's own logs (content servers, CDNs, update hosts …).
/// Each file's tail is read with sharing enabled, since Steam keeps them open.
/// </summary>
public sealed class SteamLogSource : IDomainSource
{
    /// <summary>Only the newest part of each log matters, and some grow to several MB.</summary>
    public const int MaxBytesPerFile = 2 * 1024 * 1024;

    private readonly Func<string?> _steamDirectory;

    /// <param name="steamDirectory">Returns the Steam install folder, or null when Steam isn't installed.</param>
    public SteamLogSource(Func<string?> steamDirectory) => _steamDirectory = steamDirectory;

    public string Name => "Steam logs";

    public async Task<IReadOnlyList<string>> FindAsync(CancellationToken ct = default)
    {
        var logs = _steamDirectory() is { } dir ? Path.Combine(dir, "logs") : null;
        if (logs is null || !Directory.Exists(logs))
            throw new UnblockException("Steam's log folder wasn't found (is Steam installed?)");

        var hosts = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(logs, "*.txt"))
            hosts.UnionWith(SteamDomains.ExtractHosts(await ReadTailAsync(file, ct).ConfigureAwait(false)));
        return hosts.ToList();
    }

    private static async Task<string> ReadTailAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxBytesPerFile) stream.Seek(-MaxBytesPerFile, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Steam's install folder from the registry (<c>HKCU\Software\Valve\Steam\SteamPath</c>).</summary>
    public static string? FindSteamDirectory()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamPath") is string path && path.Length > 0
            ? Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar))
            : null;
    }
}

/// <summary>
/// Steam hosts referenced by the live store and community front pages. This is how a CDN switch shows
/// up (e.g. assets moving to *.fastly.steamstatic.com) before any log or preset knows about it.
/// </summary>
public sealed class SteamPageSource : IDomainSource
{
    public static readonly IReadOnlyList<Uri> Pages = new[]
    {
        new Uri("https://store.steampowered.com/"),
        new Uri("https://steamcommunity.com/"),
    };

    private readonly HttpClient _http;

    public SteamPageSource(HttpClient http) => _http = http;

    public string Name => "Steam pages";

    /// <summary>Throws only when no page could be loaded (both blocked or offline).</summary>
    public async Task<IReadOnlyList<string>> FindAsync(CancellationToken ct = default)
    {
        var hosts = new HashSet<string>();
        var errors = new List<string>();
        foreach (var page in Pages)
        {
            try
            {
                hosts.UnionWith(SteamDomains.ExtractHosts(await _http.GetStringAsync(page, ct).ConfigureAwait(false)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                errors.Add($"{page.Host}: {ex.Message}");
            }
        }
        if (hosts.Count == 0 && errors.Count > 0)
            throw new UnblockException($"couldn't load the Steam pages ({string.Join("; ", errors)})");
        return hosts.ToList();
    }
}
