using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FirewallBundleBlocker.Services;

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

    public static string AppDataPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FirewallBundleBlocker", fileName);

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
