using System.IO;
using System.Security.Cryptography;
using System.Net.Http;

namespace LeaveMeAlone.Services.Update;

/// <summary>
/// Downloads a release next to the running exe, verifies its SHA-256 and swaps it in.
/// Windows won't overwrite or delete a running exe but does allow renaming it, so the swap is
/// "exe → exe.old, exe.new → exe"; the app then restarts and the next launch deletes exe.old.
/// </summary>
public sealed class UpdateInstaller
{
    public const string NewSuffix = ".new";
    public const string OldSuffix = ".old";

    private readonly IUpdateHttp _http;

    public UpdateInstaller(IUpdateHttp http, string exePath)
    {
        _http = http;
        ExePath = exePath;
    }

    /// <summary>The exe being replaced (the running one in production).</summary>
    public string ExePath { get; }

    /// <exception cref="UpdateException">Download, verification or swap failed; the current exe is left as it was.</exception>
    public async Task InstallAsync(AvailableUpdate update, CancellationToken cancellationToken = default)
    {
        var staged = ExePath + NewSuffix;
        try
        {
            var expected = Checksum.Parse(
                await _http.GetStringAsync(update.ChecksumUrl, cancellationToken).ConfigureAwait(false));
            await _http.DownloadAsync(update.ExeUrl, staged, cancellationToken).ConfigureAwait(false);
            await VerifyAsync(staged, expected, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or UnauthorizedAccessException or UpdateException)
        {
            TryDelete(staged);
            throw ex as UpdateException ?? new UpdateException($"Couldn't download the update: {ex.Message}", ex);
        }
        SwapIn(staged);
    }

    /// <summary>Moves the current exe aside and <paramref name="staged"/> into its place; rolls back on failure.</summary>
    public void SwapIn(string staged)
    {
        var old = ExePath + OldSuffix;
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(ExePath, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staged);
            throw new UpdateException($"Couldn't replace the app file: {ex.Message}", ex);
        }

        try
        {
            File.Move(staged, ExePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(old, ExePath); // put the working build back before reporting
            TryDelete(staged);
            throw new UpdateException($"Couldn't install the update: {ex.Message}", ex);
        }
    }

    /// <summary>Deletes exe.old / exe.new left by a previous update. Returns how many files were removed.</summary>
    public static int CleanupLeftovers(string exePath)
    {
        var removed = 0;
        foreach (var path in new[] { exePath + OldSuffix, exePath + NewSuffix })
            if (File.Exists(path) && TryDelete(path)) removed++;
        return removed;
    }

    private static async Task VerifyAsync(string path, string expected, CancellationToken cancellationToken)
    {
        string actual;
        await using (var stream = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("The downloaded update failed its checksum check, so it was not installed.");
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // still locked (e.g. the previous process is exiting); retried next launch
        }
    }
}
