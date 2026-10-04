using System.Collections.ObjectModel;
using LeaveMeAlone.Services.Unblock;

namespace LeaveMeAlone.ViewModels;

/// <summary>One row of the "real IPs, fastest first" table.</summary>
public sealed record AddressRow(int Rank, string Address, string Latency, string Tls, bool Usable)
{
    public static AddressRow From(int rank, AddressProbe probe) => new(
        rank,
        probe.Address.ToString(),
        probe.TcpLatency is { } l ? $"{l.TotalMilliseconds:0} ms" : "unreachable",
        probe.Tls switch
        {
            TlsOutcome.Ok => "TLS ok",
            TlsOutcome.Reset => "reset (SNI-filtered)",
            TlsOutcome.CertificateMismatch => "wrong certificate",
            TlsOutcome.Timeout => "timed out",
            TlsOutcome.ConnectFailed => "no connection",
            _ => "not tested",
        },
        probe.Reachable && probe.Tls == TlsOutcome.Ok);
}

/// <summary>Display wrapper for an active unblock.</summary>
public sealed class UnblockedDomainViewModel : ObservableObject
{
    private string _health = "";

    public UnblockedDomainViewModel(UnblockedDomain model) => Model = model;

    public UnblockedDomain Model { get; }
    public string Domain => Model.Domain;

    public string MethodLabel => Model.Method == UnblockMethod.SniRelay ? "SNI RELAY" : "DNS PIN";

    public string Detail => Model.Method == UnblockMethod.SniRelay
        ? $"hosts → {Model.LoopbackAddress} → relay (split ClientHello) → {string.Join(", ", Model.Addresses)}"
        : $"hosts → {string.Join(", ", Model.Addresses)}";

    /// <summary>Live relay state (connections / last error); empty for DNS pins.</summary>
    public string Health { get => _health; set => Set(ref _health, value); }
}

public sealed class UnblockViewModel : GuardedViewModel
{
    private readonly UnblockManager _manager;
    private DomainDiagnosis? _diagnosis;
    private string _domainInput = "";

    public UnblockViewModel(UnblockManager manager, bool isElevated)
    {
        _manager = manager;
        IsElevated = isElevated;
    }

    public bool IsElevated { get; }
    public ObservableCollection<UnblockedDomainViewModel> Active { get; } = new();
    public ObservableCollection<AddressRow> RankedAddresses { get; } = new();

    public string DomainInput { get => _domainInput; set => Set(ref _domainInput, value); }

    public bool HasDiagnosis => _diagnosis != null;
    public string DiagnosisTitle => _diagnosis is null ? "" : $"{_diagnosis.Domain} — {KindLabel(_diagnosis.Kind)}";
    public string DiagnosisDetail => _diagnosis?.Detail ?? "";
    public string DiagnosisAdvice => _diagnosis is null ? "" : Advice(_diagnosis);

    public async Task InitializeAsync()
    {
        await RunGuardedAsync(async () =>
        {
            var warnings = await _manager.InitializeAsync();
            Reload();
            StatusMessage = warnings.Count > 0
                ? $"Some unblocks couldn't be restored: {string.Join(" | ", warnings)}"
                : Active.Count > 0 ? $"{Active.Count} domain(s) unblocked." : "";
        }, "Failed to restore unblocked domains");
    }

    public Task DiagnoseAsync() => RunGuardedAsync(async () =>
    {
        var domain = DomainName.Normalize(DomainInput);
        StatusMessage = $"Diagnosing {domain}…";
        ShowDiagnosis(await _manager.DiagnoseAsync(domain));
        StatusMessage = $"Diagnosed {domain}.";
    }, "Diagnosis failed");

    /// <summary>Unblocks the domain in the input box, reusing the on-screen diagnosis when it's for the same domain.</summary>
    public Task UnblockAsync() => RunGuardedAsync(async () =>
    {
        RequireElevation();
        var domain = DomainName.Normalize(DomainInput);
        var diagnosis = _diagnosis?.Domain == domain ? _diagnosis : await _manager.DiagnoseAsync(domain);
        ShowDiagnosis(diagnosis);

        var entry = await _manager.UnblockAsync(diagnosis);
        Reload();
        StatusMessage = $"Unblocked {entry.Domain} via {(entry.Method == UnblockMethod.SniRelay ? "SNI relay" : "DNS pin")}.";
    }, "Unblock failed");

    /// <summary>Diagnoses every domain of the preset and unblocks the ones that are blocked.</summary>
    public Task UnblockPresetAsync(UnblockPreset preset) => RunGuardedAsync(async () =>
    {
        RequireElevation();
        var done = new List<string>();
        var clear = new List<string>();
        var failed = new List<string>();
        foreach (var domain in preset.Domains)
        {
            StatusMessage = $"{preset.Name}: checking {domain}…";
            try
            {
                var diagnosis = await _manager.DiagnoseAsync(domain);
                if (diagnosis.Kind == BlockKind.None) { clear.Add(domain); continue; }
                await _manager.UnblockAsync(diagnosis);
                done.Add(domain);
            }
            catch (UnblockException ex)
            {
                failed.Add($"{domain} ({ex.Message})");
            }
        }
        Reload();
        StatusMessage = $"{preset.Name}: unblocked {done.Count}, already reachable {clear.Count}" +
                        (failed.Count > 0 ? $", failed: {string.Join("; ", failed)}" : ".");
    }, "Preset failed");

    public Task RemoveAsync(UnblockedDomainViewModel vm) => RunGuardedAsync(async () =>
    {
        RequireElevation();
        await _manager.RemoveAsync(vm.Domain);
        Reload();
        StatusMessage = $"{vm.Domain} is back to normal.";
    }, "Remove failed");

    public Task RemoveAllAsync() => RunGuardedAsync(async () =>
    {
        RequireElevation();
        await _manager.RemoveAllAsync();
        Reload();
        StatusMessage = "All unblocks removed; the hosts file is back to how it was.";
    }, "Remove all failed");

    /// <summary>Refreshes the per-relay health text (call on a timer while the window is open).</summary>
    public void RefreshHealth()
    {
        var status = _manager.RelayStatus().ToDictionary(s => s.Domain, StringComparer.OrdinalIgnoreCase);
        foreach (var vm in Active.Where(a => a.Model.Method == UnblockMethod.SniRelay))
        {
            vm.Health = status.TryGetValue(vm.Domain, out var s)
                ? $"relay running · {s.Connections} connection(s)" + (s.LastError is { } e ? $" · last error: {e}" : "")
                : "relay NOT running — reopen the app or unblock again";
        }
    }

    private void RequireElevation()
    {
        if (!IsElevated)
            throw new UnblockException("administrator rights are required to edit the hosts file. Restart the app and accept the UAC prompt.");
    }

    private void ShowDiagnosis(DomainDiagnosis diagnosis)
    {
        _diagnosis = diagnosis;
        DomainInput = diagnosis.Domain;
        RankedAddresses.Clear();
        foreach (var (probe, i) in diagnosis.Ranked.Select((p, i) => (p, i)))
            RankedAddresses.Add(AddressRow.From(i + 1, probe));
        OnPropertyChanged(nameof(HasDiagnosis));
        OnPropertyChanged(nameof(DiagnosisTitle));
        OnPropertyChanged(nameof(DiagnosisDetail));
        OnPropertyChanged(nameof(DiagnosisAdvice));
    }

    private void Reload()
    {
        Active.Clear();
        foreach (var domain in _manager.Domains)
            Active.Add(new UnblockedDomainViewModel(domain));
        RefreshHealth();
    }

    private static string KindLabel(BlockKind kind) => kind switch
    {
        BlockKind.None => "not blocked",
        BlockKind.Dns => "blocked by DNS",
        BlockKind.Sni => "blocked by SNI filtering",
        BlockKind.DnsAndSni => "blocked by DNS + SNI filtering",
        _ => "blocked another way",
    };

    private static string Advice(DomainDiagnosis d) => d.Kind switch
    {
        BlockKind.None => "Nothing to do — this domain works normally on this network.",
        BlockKind.Dns => "Unblock will pin the working IPs below in the hosts file. Works even when the app is closed.",
        BlockKind.Sni or BlockKind.DnsAndSni =>
            "Unblock will route this domain through the app's local relay, which splits the TLS ClientHello so the ISP " +
            "can't read the name. Encryption stays end-to-end. The relay only runs while the app is open — enable " +
            "'Start with Windows' to keep it working.",
        _ => "This can't be fixed without a VPN; the app won't change anything for it.",
    };
}
