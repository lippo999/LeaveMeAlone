using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeaveMeAlone.Services;

/// <summary>
/// Shared JSON persistence for the app's config files: enums as strings, atomic replace on save,
/// and a corrupt file is backed up (<c>.corrupt</c>) and treated as empty instead of crashing.
/// </summary>
public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string AppFolderName = "LeaveMeAlone";
    private const string LegacyAppFolderName = "FirewallBundleBlocker";

    public static string AppDataPath(string fileName) => Path.Combine(AppDataRoot(), AppFolderName, fileName);

    /// <summary>
    /// One-time move of %APPDATA%\FirewallBundleBlocker (the app's former name) to the current folder.
    /// Skipped when the new folder already exists. Returns a warning when the move fails; the old
    /// folder is then left untouched so nothing is lost.
    /// </summary>
    public static string? MigrateLegacyAppData(string? root = null)
    {
        root ??= AppDataRoot();
        var legacy = Path.Combine(root, LegacyAppFolderName);
        var current = Path.Combine(root, AppFolderName);
        if (!Directory.Exists(legacy) || Directory.Exists(current)) return null;

        try
        {
            Directory.Move(legacy, current);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't move your saved data from {legacy} to {current}: {ex.Message}";
        }
    }

    private static string AppDataRoot() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static async Task<IReadOnlyList<T>> LoadListAsync<T>(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return Array.Empty<T>();

        try
        {
            await using var stream = File.OpenRead(path);
            var data = await JsonSerializer.DeserializeAsync<List<T>>(stream, Options, ct).ConfigureAwait(false);
            return data ?? new List<T>();
        }
        catch (JsonException)
        {
            // Corrupt file: keep a copy for inspection, start clean rather than crash on launch.
            TryBackupCorruptFile(path);
            return Array.Empty<T>();
        }
    }

    public static async Task SaveAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, value, Options, ct).ConfigureAwait(false);
        }

        // Atomic replace so a crash mid-write can never corrupt the real file.
        if (File.Exists(path))
            File.Replace(tmp, path, null);
        else
            File.Move(tmp, path);
    }

    private static void TryBackupCorruptFile(string path)
    {
        try { File.Copy(path, path + ".corrupt", overwrite: true); }
        catch (IOException) { /* best-effort: the load already falls back to empty */ }
        catch (UnauthorizedAccessException) { }
    }
}
