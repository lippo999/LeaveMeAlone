namespace LeaveMeAlone.Services.Unblock.Scan;

/// <summary>A Steam domain the scan found blocked, with where it was seen.</summary>
public sealed record ScanFinding(DomainDiagnosis Diagnosis, IReadOnlyList<string> Sources)
{
    public string Domain => Diagnosis.Domain;
}

/// <param name="Blocked">Fixable findings (DNS and/or SNI blocked, with a working IP), sorted by domain.</param>
/// <param name="Checked">How many candidate domains were diagnosed.</param>
/// <param name="Problems">Sources that couldn't be read and domains that couldn't be checked.</param>
public sealed record ScanResult(IReadOnlyList<ScanFinding> Blocked, int Checked, IReadOnlyList<string> Problems);

/// <summary>
/// "Scan &amp; fix" for Steam: gathers candidate hosts (Steam presets + the discovery sources), keeps only
/// Steam domains that aren't rerouted yet, diagnoses them and returns the ones that are blocked and fixable.
/// Nothing is changed here; the caller lets the user confirm and then unblocks the chosen findings.
/// </summary>
public sealed class SteamScanner
{
    public const int MaxParallelDiagnoses = 6;
    public const string PresetSource = "Steam preset";

    private readonly UnblockManager _manager;
    private readonly IReadOnlyList<IDomainSource> _sources;
    private readonly IReadOnlyList<string> _knownDomains;

    /// <param name="knownDomains">Preset domains that are always checked (defaults to every Steam preset).</param>
    public SteamScanner(UnblockManager manager, IReadOnlyList<IDomainSource> sources, IReadOnlyList<string>? knownDomains = null)
    {
        _manager = manager;
        _sources = sources;
        _knownDomains = knownDomains ?? UnblockPresets.All.SelectMany(p => p.Domains).ToList();
    }

    public async Task<ScanResult> ScanAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var candidates = await GatherAsync(problems, progress, ct).ConfigureAwait(false);

        var counter = new ProgressCounter(candidates.Count, progress);
        using var gate = new SemaphoreSlim(MaxParallelDiagnoses);
        var diagnoses = await Task.WhenAll(candidates.Keys
            .Select(domain => DiagnoseOneAsync(domain, gate, counter, problems, ct)))
            .ConfigureAwait(false);

        var blocked = diagnoses
            .OfType<DomainDiagnosis>()
            .Where(IsFixable)
            .Select(d => new ScanFinding(d, candidates[d.Domain]))
            .OrderBy(f => f.Domain, StringComparer.Ordinal)
            .ToList();
        return new ScanResult(blocked, candidates.Count, problems);
    }

    // domain → names of the sources that mentioned it. Steam-only, minus what's already rerouted.
    private async Task<Dictionary<string, IReadOnlyList<string>>> GatherAsync(
        List<string> problems, IProgress<string>? progress, CancellationToken ct)
    {
        var seen = new Dictionary<string, List<string>>();
        void Add(string domain, string source)
        {
            if (!SteamDomains.IsSteam(domain)) return; // scope guard: never touch non-Steam domains
            var key = DomainName.Normalize(domain);
            if (!seen.TryGetValue(key, out var list)) seen[key] = list = new List<string>();
            if (!list.Contains(source)) list.Add(source);
        }

        foreach (var domain in _knownDomains) Add(domain, PresetSource);
        foreach (var source in _sources)
        {
            progress?.Report($"Looking for Steam servers ({source.Name})…");
            try
            {
                foreach (var domain in await source.FindAsync(ct).ConfigureAwait(false)) Add(domain, source.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"{source.Name}: {ex.Message}");
            }
        }

        var active = _manager.Domains.Select(d => d.Domain).ToHashSet();
        return seen
            .Where(kv => !active.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    // A domain that can't be diagnosed is reported, not fatal: the rest of the scan still counts.
    private async Task<DomainDiagnosis?> DiagnoseOneAsync(
        string domain, SemaphoreSlim gate, ProgressCounter counter, List<string> problems, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _manager.DiagnoseAsync(domain, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (problems) problems.Add($"{domain}: {ex.Message}");
            return null;
        }
        finally
        {
            gate.Release();
            counter.Step();
        }
    }

    private sealed class ProgressCounter(int total, IProgress<string>? progress)
    {
        private int _done;

        public void Step() =>
            progress?.Report($"Checking Steam servers… {Interlocked.Increment(ref _done)} of {total}");
    }

    private static bool IsFixable(DomainDiagnosis d) =>
        d.Kind is BlockKind.Dns or BlockKind.Sni or BlockKind.DnsAndSni && d.UsableAddresses.Count > 0;
}
