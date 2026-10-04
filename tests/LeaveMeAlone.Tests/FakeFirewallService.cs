using LeaveMeAlone.Models;
using LeaveMeAlone.Services;

namespace LeaveMeAlone.Tests;

/// <summary>
/// In-memory <see cref="IFirewallService"/> for tests. Records every call and simulates the
/// set of live rules per bundle so behavior can be asserted without touching Windows Firewall.
/// </summary>
public sealed class FakeFirewallService : IFirewallService
{
    public List<string> Calls { get; } = new();
    private readonly Dictionary<string, HashSet<string>> _live = new();

    /// <summary>When set, the next <see cref="ApplyBundleAsync"/> throws (after recording the call).</summary>
    public bool FailNextApply { get; set; }

    public Task ApplyBundleAsync(Bundle bundle, CancellationToken ct = default)
    {
        Calls.Add($"ApplyBundle:{bundle.Id}");
        if (FailNextApply)
        {
            FailNextApply = false;
            throw new FirewallException("simulated failure");
        }
        _live[bundle.Id] = bundle.Targets
            .Select(t => FirewallRuleNaming.DisplayNameFor(bundle.Id, t.Id))
            .ToHashSet();
        return Task.CompletedTask;
    }

    public Task ApplyTargetAsync(Bundle bundle, FirewallTarget target, CancellationToken ct = default)
    {
        Calls.Add($"ApplyTarget:{bundle.Id}:{target.Id}");
        if (!_live.TryGetValue(bundle.Id, out var set))
            _live[bundle.Id] = set = new HashSet<string>();
        set.Add(FirewallRuleNaming.DisplayNameFor(bundle.Id, target.Id));
        return Task.CompletedTask;
    }

    public Task RemoveTargetAsync(string bundleId, string targetId, CancellationToken ct = default)
    {
        Calls.Add($"RemoveTarget:{bundleId}:{targetId}");
        if (_live.TryGetValue(bundleId, out var set))
            set.Remove(FirewallRuleNaming.DisplayNameFor(bundleId, targetId));
        return Task.CompletedTask;
    }

    public Task SetBundleEnabledAsync(string bundleId, bool enabled, CancellationToken ct = default)
    {
        Calls.Add($"SetEnabled:{bundleId}:{enabled}");
        return Task.CompletedTask;
    }

    public Task RemoveBundleAsync(string bundleId, CancellationToken ct = default)
    {
        Calls.Add($"RemoveBundle:{bundleId}");
        _live.Remove(bundleId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetLiveRuleDisplayNamesAsync(string bundleId, CancellationToken ct = default)
    {
        IReadOnlyList<string> names = _live.TryGetValue(bundleId, out var set) ? set.ToList() : new List<string>();
        return Task.FromResult(names);
    }
}
