using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace LeaveMeAlone.Services.Update;

/// <summary>Real <see cref="IUpdateHttp"/> over HTTPS (GitHub requires a User-Agent on API calls).</summary>
public sealed class GitHubUpdateHttp : IUpdateHttp, IDisposable
{
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _client;

    public GitHubUpdateHttp(Version appVersion)
    {
        _client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // per-call timeouts below
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LeaveMeAlone", UpdateSource.Display(appVersion)));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        using var timeout = Linked(cancellationToken, ApiTimeout);
        using var response = await _client.GetAsync(uri, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    public async Task DownloadAsync(Uri uri, string destinationPath, CancellationToken cancellationToken = default)
    {
        using var timeout = Linked(cancellationToken, DownloadTimeout);
        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, timeout.Token).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();

    private static CancellationTokenSource Linked(CancellationToken token, TimeSpan after)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(after);
        return cts;
    }
}
