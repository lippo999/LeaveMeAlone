namespace LeaveMeAlone.Services.Update;

/// <summary>A newer stable release found on GitHub, with the files needed to install it.</summary>
public sealed record AvailableUpdate(Version Version, string Tag, Uri ExeUrl, Uri ChecksumUrl, Uri PageUrl);

/// <summary>Checking, downloading or installing an update failed; the message is shown to the user.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message) { }
    public UpdateException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The network seam for updates (real impl: <see cref="GitHubUpdateHttp"/>; tests use a fake).</summary>
public interface IUpdateHttp
{
    Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default);

    /// <summary>Streams <paramref name="uri"/> to <paramref name="destinationPath"/> (overwriting it).</summary>
    Task DownloadAsync(Uri uri, string destinationPath, CancellationToken cancellationToken = default);
}

/// <summary>Where releases are published (the CI workflow attaches these two files to every v* release).</summary>
public static class UpdateSource
{
    public const string Repository = "lippo999/LeaveMeAlone";
    public const string ExeAssetName = "LeaveMeAlone.exe";
    public const string ChecksumAssetName = "LeaveMeAlone.exe.sha256";

    public static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>"1.2" / "1.2.3" / "1.2.3.0" → "1.2.3" (Version.ToString(3) throws when a part is missing).</summary>
    public static string Display(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
}
