namespace LeaveMeAlone.Services.Unblock;

public interface IUnblockStore
{
    Task<IReadOnlyList<UnblockedDomain>> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(IReadOnlyList<UnblockedDomain> domains, CancellationToken ct = default);
}

/// <summary>
/// Persists unblocked domains at %APPDATA%\LeaveMeAlone\unblocked.json.
/// Schema: [ { "Domain": "store.steampowered.com", "Method": "SniRelay", "DetectedKind": "DnsAndSni",
///             "Addresses": ["104.83.198.104"], "LoopbackAddress": "127.77.0.1", "UnblockedAt": "..." } ]
/// </summary>
public sealed class JsonUnblockStore : IUnblockStore
{
    public JsonUnblockStore(string? filePath = null)
    {
        FilePath = filePath ?? JsonFile.AppDataPath("unblocked.json");
    }

    public string FilePath { get; }

    public Task<IReadOnlyList<UnblockedDomain>> LoadAsync(CancellationToken ct = default)
        => JsonFile.LoadListAsync<UnblockedDomain>(FilePath, ct);

    public Task SaveAsync(IReadOnlyList<UnblockedDomain> domains, CancellationToken ct = default)
        => JsonFile.SaveAsync(FilePath, domains, ct);
}
