using System.Net;

namespace FirewallBundleBlocker.Services.Unblock;

/// <summary>
/// Orchestrates domain unblocking: picks the method for the detected block, keeps the hosts-file
/// block, the relay listeners and unblocked.json in sync, and undoes everything on removal.
/// Mirrors <see cref="BundleManager"/>: immutable records, list replaced rather than edited.
/// </summary>
public sealed class UnblockManager
{
    /// <summary>Upper bound of real IPs written per domain (hosts lines / relay fallbacks).</summary>
    public const int MaxAddressesPerDomain = 4;
    private const string LoopbackPrefix = "127.77.0.";

    private readonly IUnblockStore _store;
    private readonly IHostsFile _hosts;
    private readonly IDomainDiagnoser _diagnoser;
    private readonly IRelayHost _relay;
    private readonly Func<DateTimeOffset> _clock;
    private List<UnblockedDomain> _domains = new();

    public UnblockManager(IUnblockStore store, IHostsFile hosts, IDomainDiagnoser diagnoser, IRelayHost relay,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _hosts = hosts;
        _diagnoser = diagnoser;
        _relay = relay;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public IReadOnlyList<UnblockedDomain> Domains => _domains;

    /// <summary>
    /// Loads saved domains, starts their relays and rewrites the hosts block. A relay that can't start
    /// is left out of the hosts file (so that domain simply behaves as before) and reported as a warning.
    /// </summary>
    public async Task<IReadOnlyList<string>> InitializeAsync(CancellationToken ct = default)
    {
        _domains = (await _store.LoadAsync(ct).ConfigureAwait(false)).ToList();
        var warnings = new List<string>();
        var active = new List<UnblockedDomain>();
        foreach (var domain in _domains)
        {
            try
            {
                if (domain.Method == UnblockMethod.SniRelay) _relay.Start(RouteFor(domain));
                active.Add(domain);
            }
            catch (UnblockException ex)
            {
                warnings.Add(ex.Message);
            }
        }
        await WriteHostsAsync(active, ct).ConfigureAwait(false);
        return warnings;
    }

    public Task<DomainDiagnosis> DiagnoseAsync(string domain, CancellationToken ct = default)
        => _diagnoser.DiagnoseAsync(domain, ct);

    public async Task<UnblockedDomain> UnblockAsync(string domain, CancellationToken ct = default)
        => await UnblockAsync(await DiagnoseAsync(domain, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    /// <summary>Unblocks (or refreshes) a domain using a fresh diagnosis. Throws <see cref="UnblockException"/> when it can't.</summary>
    public async Task<UnblockedDomain> UnblockAsync(DomainDiagnosis diagnosis, CancellationToken ct = default)
    {
        var addresses = diagnosis.UsableAddresses.Take(MaxAddressesPerDomain).Select(a => a.ToString()).ToList();
        EnsureUnblockable(diagnosis, addresses);
        await EnsureNoForeignHostsEntryAsync(diagnosis.Domain, ct).ConfigureAwait(false);

        var previous = Find(diagnosis.Domain);
        var entry = new UnblockedDomain
        {
            Domain = diagnosis.Domain,
            DetectedKind = diagnosis.Kind,
            Method = diagnosis.SniBlocked ? UnblockMethod.SniRelay : UnblockMethod.HostsPin,
            Addresses = addresses,
            LoopbackAddress = diagnosis.SniBlocked ? previous?.LoopbackAddress ?? AllocateLoopback() : null,
            UnblockedAt = _clock(),
        };

        var updated = previous is null
            ? _domains.Append(entry).ToList()
            : _domains.Select(d => d.Domain == entry.Domain ? entry : d).ToList();

        await CommitAsync(updated, started: entry, stopped: previous, ct).ConfigureAwait(false);
        return entry;
    }

    public async Task RemoveAsync(string domain, CancellationToken ct = default)
    {
        var existing = Find(domain) ?? throw new UnblockException($"{domain} isn't unblocked.");
        var updated = _domains.Where(d => d.Domain != existing.Domain).ToList();
        await CommitAsync(updated, started: null, stopped: existing, ct).ConfigureAwait(false);
    }

    /// <summary>Removes every unblock: stops all relays and deletes the app's hosts block entirely.</summary>
    public async Task RemoveAllAsync(CancellationToken ct = default)
    {
        _relay.StopAll();
        await WriteHostsAsync(Array.Empty<UnblockedDomain>(), ct).ConfigureAwait(false);
        _domains = new List<UnblockedDomain>();
        await _store.SaveAsync(_domains, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Called on app exit. Relays stop with the app, so their hosts lines are removed too — otherwise
    /// those domains would point at a dead loopback address until the next launch. Pins stay.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        _relay.StopAll();
        await WriteHostsAsync(_domains.Where(d => d.Method == UnblockMethod.HostsPin).ToList(), ct).ConfigureAwait(false);
    }

    public IReadOnlyList<RelayStatus> RelayStatus() => _relay.Status();

    // Applies relay → hosts → store, undoing the earlier steps if a later one fails.
    private async Task CommitAsync(List<UnblockedDomain> updated, UnblockedDomain? started, UnblockedDomain? stopped, CancellationToken ct)
    {
        if (stopped?.Method == UnblockMethod.SniRelay) _relay.Stop(stopped.Domain);
        try
        {
            if (started?.Method == UnblockMethod.SniRelay) _relay.Start(RouteFor(started));
            await WriteHostsAsync(updated, ct).ConfigureAwait(false);
        }
        catch
        {
            if (started?.Method == UnblockMethod.SniRelay) _relay.Stop(started.Domain);
            if (stopped?.Method == UnblockMethod.SniRelay) TryRestart(stopped);
            throw;
        }

        try
        {
            await _store.SaveAsync(updated, ct).ConfigureAwait(false);
        }
        catch
        {
            // Hosts already reflects the new state; leaving it while the saved list doesn't would make
            // the next launch "forget" a change it can no longer undo, so put the hosts block back.
            await WriteHostsAsync(_domains, ct).ConfigureAwait(false);
            throw;
        }
        _domains = updated;
    }

    private void TryRestart(UnblockedDomain domain)
    {
        try { _relay.Start(RouteFor(domain)); }
        catch (UnblockException) { /* the original error is what gets reported */ }
    }

    private async Task WriteHostsAsync(IReadOnlyList<UnblockedDomain> domains, CancellationToken ct)
    {
        var current = await _hosts.ReadAsync(ct).ConfigureAwait(false);
        var desired = HostsBlock.Apply(current, HostsEntries(domains));
        if (desired != current) await _hosts.WriteAsync(desired, ct).ConfigureAwait(false);
    }

    public static IReadOnlyList<HostsEntry> HostsEntries(IEnumerable<UnblockedDomain> domains) => domains
        .SelectMany(d => d.Method == UnblockMethod.SniRelay
            ? new[] { new HostsEntry(d.LoopbackAddress!, d.Domain) }
            : d.Addresses.Select(a => new HostsEntry(a, d.Domain)))
        .ToList();

    private static void EnsureUnblockable(DomainDiagnosis diagnosis, IReadOnlyList<string> addresses)
    {
        if (diagnosis.Kind == BlockKind.None)
            throw new UnblockException($"{diagnosis.Domain} isn't blocked on this network — nothing to unblock.");
        if (diagnosis.Kind == BlockKind.Other)
            throw new UnblockException($"Can't unblock {diagnosis.Domain}: {diagnosis.Detail}");
        if (addresses.Count == 0)
            throw new UnblockException($"No working IP was found for {diagnosis.Domain}.");
    }

    private async Task EnsureNoForeignHostsEntryAsync(string domain, CancellationToken ct)
    {
        var content = await _hosts.ReadAsync(ct).ConfigureAwait(false);
        if (HostsBlock.HasForeignEntry(content, domain))
            throw new UnblockException(
                $"The hosts file already has its own entry for {domain} (outside the app's block). " +
                "Remove that line first so the two don't conflict.");
    }

    private string AllocateLoopback()
    {
        var used = _domains.Select(d => d.LoopbackAddress).Where(a => a != null).ToHashSet();
        for (int i = 1; i <= 254; i++)
        {
            var candidate = LoopbackPrefix + i;
            if (!used.Contains(candidate)) return candidate;
        }
        throw new UnblockException("Too many relayed domains (limit 254).");
    }

    private UnblockedDomain? Find(string domain)
    {
        var normalized = DomainName.Normalize(domain);
        return _domains.FirstOrDefault(d => d.Domain == normalized);
    }

    private static RelayRoute RouteFor(UnblockedDomain domain) => new(
        domain.Domain,
        IPAddress.Parse(domain.LoopbackAddress ?? throw new UnblockException($"{domain.Domain} has no relay address.")),
        domain.Addresses.Select(IPAddress.Parse).ToList());
}
