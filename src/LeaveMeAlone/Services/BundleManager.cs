using LeaveMeAlone.Models;

namespace LeaveMeAlone.Services;

/// <summary>
/// Coordinates the bundle store and the firewall. Holds the in-memory list of bundles and
/// keeps the persisted config and the live firewall rules in sync. All mutations are immutable
/// (records + new lists) so callers never observe half-applied state.
/// </summary>
public sealed class BundleManager
{
    private readonly IBundleStore _store;
    private readonly IFirewallService _firewall;
    private List<Bundle> _bundles = new();

    public BundleManager(IBundleStore store, IFirewallService firewall)
    {
        _store = store;
        _firewall = firewall;
    }

    public IReadOnlyList<Bundle> Bundles => _bundles;

    /// <summary>Loads persisted bundles and reconciles the firewall to match (self-heals missing rules).</summary>
    public async Task<IReadOnlyList<Bundle>> InitializeAsync(CancellationToken ct = default)
    {
        _bundles = (await _store.LoadAsync(ct).ConfigureAwait(false)).ToList();
        foreach (var bundle in _bundles)
            await _firewall.ApplyBundleAsync(bundle, ct).ConfigureAwait(false);
        return _bundles;
    }

    public async Task<Bundle> CreateBundleAsync(string name, CancellationToken ct = default)
    {
        var bundle = new Bundle { Name = name.Trim(), Enabled = false };
        _bundles = Append(_bundles, bundle);
        await _store.SaveAsync(_bundles, ct).ConfigureAwait(false);
        return bundle;
    }

    public async Task<Bundle> RenameBundleAsync(string bundleId, string name, CancellationToken ct = default)
    {
        var updated = Get(bundleId) with { Name = name.Trim() };
        _bundles = ReplaceInstance(updated);
        await _store.SaveAsync(_bundles, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task DeleteBundleAsync(string bundleId, CancellationToken ct = default)
    {
        await _firewall.RemoveBundleAsync(bundleId, ct).ConfigureAwait(false);
        _bundles = _bundles.Where(b => b.Id != bundleId).ToList();
        await _store.SaveAsync(_bundles, ct).ConfigureAwait(false);
    }

    public async Task<Bundle> SetEnabledAsync(string bundleId, bool enabled, CancellationToken ct = default)
    {
        var updated = Get(bundleId) with { Enabled = enabled };
        // Re-apply the whole bundle so rules exist and reflect the new enabled state
        // (covers the first enable and rules cleared outside the app).
        await _firewall.ApplyBundleAsync(updated, ct).ConfigureAwait(false);
        _bundles = ReplaceInstance(updated);
        await _store.SaveAsync(_bundles, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<Bundle> AddTargetAsync(string bundleId, FirewallTarget target, CancellationToken ct = default)
    {
        var current = Get(bundleId);
        var updated = current with { Targets = Append(current.Targets, target) };
        await _firewall.ApplyTargetAsync(updated, target, ct).ConfigureAwait(false);
        _bundles = ReplaceInstance(updated);
        await _store.SaveAsync(_bundles, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<Bundle> RemoveTargetAsync(string bundleId, string targetId, CancellationToken ct = default)
    {
        var current = Get(bundleId);
        var updated = current with { Targets = current.Targets.Where(t => t.Id != targetId).ToList() };
        await _firewall.RemoveTargetAsync(bundleId, targetId, ct).ConfigureAwait(false);
        _bundles = ReplaceInstance(updated);
        await _store.SaveAsync(_bundles, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>Display names of the live firewall rules currently owned by this bundle.</summary>
    public Task<IReadOnlyList<string>> GetLiveRuleNamesAsync(string bundleId, CancellationToken ct = default)
        => _firewall.GetLiveRuleDisplayNamesAsync(bundleId, ct);

    /// <summary>Writes all current bundles to a chosen file (JSON).</summary>
    public Task ExportAsync(string path, CancellationToken ct = default)
        => new JsonBundleStore(path).SaveAsync(_bundles, ct);

    /// <summary>
    /// Reads bundles from a file and appends them with fresh IDs (so imported bundles never
    /// collide with existing ones or their firewall groups). Returns the number imported.
    /// </summary>
    public async Task<int> ImportAsync(string path, CancellationToken ct = default)
    {
        var incoming = await new JsonBundleStore(path).LoadAsync(ct).ConfigureAwait(false);
        var added = incoming.Select(WithFreshIds).ToList();

        foreach (var bundle in added)
            await _firewall.ApplyBundleAsync(bundle, ct).ConfigureAwait(false);

        _bundles = _bundles.Concat(added).ToList();
        await _store.SaveAsync(_bundles, ct).ConfigureAwait(false);
        return added.Count;
    }

    private static Bundle WithFreshIds(Bundle bundle) => bundle with
    {
        Id = Guid.NewGuid().ToString("N"),
        Targets = bundle.Targets.Select(t => t with { Id = Guid.NewGuid().ToString("N") }).ToList(),
    };

    private Bundle Get(string bundleId) =>
        _bundles.FirstOrDefault(b => b.Id == bundleId)
        ?? throw new InvalidOperationException($"Bundle '{bundleId}' not found.");

    private List<Bundle> ReplaceInstance(Bundle updated) =>
        _bundles.Select(b => b.Id == updated.Id ? updated : b).ToList();

    private static List<T> Append<T>(IEnumerable<T> source, T item) => source.Concat(new[] { item }).ToList();
}
