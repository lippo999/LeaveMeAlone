using FirewallBundleBlocker.Models;

namespace FirewallBundleBlocker.Services;

public interface IBundleStore
{
    string FilePath { get; }
    Task<IReadOnlyList<Bundle>> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(IReadOnlyList<Bundle> bundles, CancellationToken ct = default);
}

/// <summary>
/// Persists bundles as JSON. Default location: %APPDATA%\FirewallBundleBlocker\bundles.json.
///
/// Schema (array of bundles; enums serialized as strings; no dates are stored):
/// [
///   {
///     "Id": "9f3c...",          // GUID (32 hex chars)
///     "Name": "Steam",
///     "Enabled": true,
///     "Targets": [
///       { "Id": "a1b2...", "Type": "Program",       "Value": "C:\\...\\steam.exe",
///         "Protocol": "Any", "DisplayName": "Steam — steam.exe" },
///       { "Id": "c3d4...", "Type": "RemoteAddress",  "Value": "203.0.113.0/24",
///         "RemotePort": "27015", "Protocol": "TCP", "DisplayName": "Steam servers" }
///     ]
///   }
/// ]
/// </summary>
public sealed class JsonBundleStore : IBundleStore
{
    public string FilePath { get; }

    public JsonBundleStore(string? filePath = null)
    {
        FilePath = filePath ?? JsonFile.AppDataPath("bundles.json");
    }

    public Task<IReadOnlyList<Bundle>> LoadAsync(CancellationToken ct = default)
        => JsonFile.LoadListAsync<Bundle>(FilePath, ct);

    public Task SaveAsync(IReadOnlyList<Bundle> bundles, CancellationToken ct = default)
        => JsonFile.SaveAsync(FilePath, bundles, ct);
}
