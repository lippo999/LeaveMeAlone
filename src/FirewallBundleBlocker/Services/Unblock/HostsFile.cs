using System.IO;
using System.Text;

namespace FirewallBundleBlocker.Services.Unblock;

public sealed record HostsEntry(string Address, string Domain);

/// <summary>
/// Pure text operations on the hosts file. The app owns exactly one marked block and never
/// touches anything outside it, so removing the block restores the file byte-for-byte.
/// </summary>
public static class HostsBlock
{
    public const string BeginMarker = "# >>> FirewallBundleBlocker unblock (managed by the app - do not edit) >>>";
    public const string EndMarker = "# <<< FirewallBundleBlocker unblock <<<";

    // Appended to the begin marker when we had to add a line break before the block
    // (original file didn't end with one), so removal can take that break back out too.
    private const string AddedBreakFlag = " +nl";

    /// <summary>Writes <paramref name="entries"/> into our block (replaced in place if present, else appended); no entries removes it.</summary>
    public static string Apply(string content, IReadOnlyList<HostsEntry> entries)
    {
        var existing = Locate(content);
        if (entries.Count == 0) return Remove(content);

        if (existing is { } block)
        {
            var nl = DetectNewline(content);
            return content[..block.BlockStart] + Render(entries, nl, block.AddedBreak) + content[block.End..];
        }

        var newline = DetectNewline(content);
        bool needsBreak = content.Length > 0 && !content.EndsWith('\n');
        return content + (needsBreak ? newline : "") + Render(entries, newline, needsBreak);
    }

    public static string Remove(string content) => Locate(content) is { } block
        ? content[..block.Start] + content[block.End..]
        : content;

    private static string Render(IReadOnlyList<HostsEntry> entries, string nl, bool addedBreak)
    {
        var sb = new StringBuilder();
        sb.Append(BeginMarker).Append(addedBreak ? AddedBreakFlag : "").Append(nl);
        foreach (var entry in entries)
            sb.Append(entry.Address).Append(' ').Append(entry.Domain).Append(nl);
        sb.Append(EndMarker).Append(nl);
        return sb.ToString();
    }

    // Start includes the line break we added before the block (if any); BlockStart is the marker line.
    private readonly record struct BlockSpan(int Start, int BlockStart, int End, bool AddedBreak);

    private static BlockSpan? Locate(string content)
    {
        int begin = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0) return null;

        int end = content.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        int endOfBlock = end < 0 ? content.Length : LineEnd(content, end + EndMarker.Length);
        bool addedBreak = content.AsSpan(begin + BeginMarker.Length).StartsWith(AddedBreakFlag, StringComparison.Ordinal);
        int start = addedBreak ? StartOfPrecedingBreak(content, begin) : begin;
        return new BlockSpan(start, begin, endOfBlock, addedBreak);
    }

    /// <summary>True if a line outside our block already maps <paramref name="domain"/> (it would win or conflict).</summary>
    public static bool HasForeignEntry(string content, string domain) => Remove(content)
        .Split('\n')
        .Select(line => line.Split('#')[0].Trim())
        .Where(line => line.Length > 0)
        .Any(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Any(name => string.Equals(name, domain, StringComparison.OrdinalIgnoreCase)));

    private static int LineEnd(string content, int index)
    {
        int newline = content.IndexOf('\n', index);
        return newline < 0 ? content.Length : newline + 1;
    }

    private static int StartOfPrecedingBreak(string content, int index)
    {
        if (index >= 2 && content[index - 2] == '\r' && content[index - 1] == '\n') return index - 2;
        if (index >= 1 && content[index - 1] == '\n') return index - 1;
        return index;
    }

    private static string DetectNewline(string content)
        => content.Contains('\n') && !content.Contains("\r\n") ? "\n" : "\r\n";
}

public interface IHostsFile
{
    Task<string> ReadAsync(CancellationToken ct = default);

    /// <summary>Writes the file and flushes the OS DNS cache so the change takes effect immediately.</summary>
    Task WriteAsync(string content, CancellationToken ct = default);
}

/// <summary>
/// The real %SystemRoot%\System32\drivers\etc\hosts. Bytes round-trip exactly (Latin-1, or UTF-8
/// when the file has a BOM), and a copy of the previous version is kept in app data before each write.
/// </summary>
public sealed class WindowsHostsFile : IHostsFile
{
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };
    private readonly IProcessRunner _runner;
    private readonly string _backupPath;
    private bool _utf8Bom;

    public WindowsHostsFile(IProcessRunner runner, string? path = null, string? backupPath = null)
    {
        _runner = runner;
        FilePath = path ?? Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        _backupPath = backupPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FirewallBundleBlocker", "hosts.backup");
    }

    public string FilePath { get; }

    public async Task<string> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath)) return "";
        var bytes = await File.ReadAllBytesAsync(FilePath, ct).ConfigureAwait(false);
        _utf8Bom = bytes.AsSpan().StartsWith(Utf8Bom);
        return _utf8Bom ? Encoding.UTF8.GetString(bytes, Utf8Bom.Length, bytes.Length - Utf8Bom.Length) : Encoding.Latin1.GetString(bytes);
    }

    public async Task WriteAsync(string content, CancellationToken ct = default)
    {
        var body = _utf8Bom ? Utf8Bom.Concat(Encoding.UTF8.GetBytes(content)).ToArray() : Encoding.Latin1.GetBytes(content);
        try
        {
            if (File.Exists(FilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
                File.Copy(FilePath, _backupPath, overwrite: true);
            }
            // Truncate in place (rather than replace) so the file keeps its ACL and attributes.
            await using var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnblockException(
                $"Administrator rights are required to edit the hosts file ({FilePath}). " +
                "Restart the app and accept the administrator prompt. If it is already elevated, check that the " +
                "hosts file isn't read-only or locked by antivirus.", ex);
        }
        catch (IOException ex)
        {
            throw new UnblockException($"Could not write the hosts file ({FilePath}): {ex.Message}", ex);
        }

        var flush = await _runner.RunAsync("ipconfig.exe", "/flushdns", ct).ConfigureAwait(false);
        if (flush.ExitCode != 0)
            throw new UnblockException($"The hosts file was updated but flushing the DNS cache failed: {flush.StdOut}{flush.StdErr}".Trim());
    }
}
