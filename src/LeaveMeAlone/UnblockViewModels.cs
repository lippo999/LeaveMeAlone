using System.Collections.ObjectModel;
using LeaveMeAlone.Services;
using LeaveMeAlone.Services.Unblock;
using LeaveMeAlone.Services.Unblock.Scan;

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
    private bool _relayDown;

    public UnblockedDomainViewModel(UnblockedDomain model) => Model = model;

    public UnblockedDomain Model { get; }
    public string Domain => Model.Domain;

    public string MethodLabel => Model.Method == UnblockMethod.SniRelay ? "SNI RELAY" : "DNS PIN";

    public string Detail => Model.Method == UnblockMethod.SniRelay
        ? $"hosts → {Model.LoopbackAddress} → relay (split ClientHello) → {string.Join(", ", Model.Addresses)}"
        : $"hosts → {string.Join(", ", Model.Addresses)}";

    /// <summary>Live relay state (connections / last error); empty for DNS pins.</summary>
    public string Health { get => _health; set => Set(ref _health, value); }

    /// <summary>True when this domain needs a relay that isn't running (shown as a warning).</summary>
    public bool RelayDown { get => _relayDown; set => Set(ref _relayDown, value); }
}

/// <summary>One switch on the Quick unblock screen.</summary>
public sealed class UnblockPresetViewModel : ObservableObject
{
    private int _activeCount;
    private string _note = "";

    public UnblockPresetViewModel(UnblockPreset preset) => Preset = preset;

    public UnblockPreset Preset { get; }
    public string Title => Preset.Name;
    public string Description => Preset.Description;
    public bool IsOn => _activeCount > 0;

    /// <summary>Progress or the outcome of the last run; shown instead of the default status while set.</summary>
    public string Note { get => _note; set { if (Set(ref _note, value)) OnPropertyChanged(nameof(StatusText)); } }

    public string StatusText => Note.Length > 0 ? Note
        : IsOn ? $"On · {_activeCount} of {Preset.Domains.Count} servers rerouted"
        : "Off";

    /// <summary>Always raises change events so a switch the user flipped snaps back if the change failed.</summary>
    public void SetActiveCount(int count)
    {
        _activeCount = count;
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(StatusText));
    }
}

public sealed class UnblockViewModel : GuardedViewModel
{
    private readonly UnblockManager _manager;
    private readonly SteamScanner? _scanner;
    private readonly AppLog? _log;
    private DomainDiagnosis? _diagnosis;
    private string _domainInput = "";
    private string _scanNote = "";

    public UnblockViewModel(UnblockManager manager, bool isElevated, AppLog? log = null, SteamScanner? scanner = null)
        : base(log, "Unblock")
    {
        _manager = manager;
        _scanner = scanner;
        _log = log;
        IsElevated = isElevated;
        foreach (var preset in UnblockPresets.All)
            Presets.Add(new UnblockPresetViewModel(preset));
    }

    public bool IsElevated { get; }
    public bool CanScan => IsElevated && _scanner != null;

    /// <summary>Progress or outcome of the last Steam scan, shown on its card.</summary>
    public string ScanNote
    {
        get => _scanNote;
        private set { if (Set(ref _scanNote, value)) OnPropertyChanged(nameof(HasScanNote)); }
    }

    public bool HasScanNote => ScanNote.Length > 0;
    public ObservableCollection<UnblockPresetViewModel> Presets { get; } = new();
    public ObservableCollection<UnblockedDomainViewModel> Active { get; } = new();

    public bool HasActive => Active.Count > 0;
    public string Summary => Active.Count switch
    {
        0 => "Nothing rerouted",
        1 => "1 domain rerouted",
        var n => $"{n} domains rerouted",
    };
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
            // Only problems are reported here: App copies this into the main status line on startup.
            StatusMessage = warnings.Count > 0
                ? $"Some unblocks couldn't be restored: {string.Join(" | ", warnings)}"
                : "";
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

    /// <summary>Quick-screen switch: on = unblock whatever the preset finds blocked, off = undo the preset's unblocks.</summary>
    public Task TogglePresetAsync(UnblockPresetViewModel row, bool on) => on ? RunPresetAsync(row) : RemovePresetAsync(row);

    /// <summary>Diagnoses every domain of the preset and unblocks the ones that are blocked.</summary>
    private async Task RunPresetAsync(UnblockPresetViewModel row)
    {
        var preset = row.Preset;
        await RunGuardedAsync(async () =>
        {
            RequireElevation();
            var done = new List<string>();
            var clear = new List<string>();
            var failed = new List<string>();
            try
            {
                foreach (var (domain, i) in preset.Domains.Select((d, i) => (d, i)))
                {
                    row.Note = $"Checking {i + 1} of {preset.Domains.Count}…";
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
            }
            finally
            {
                row.Note = "";
            }
            if (done.Count == 0 && failed.Count == 0) row.Note = "Nothing is blocked on this network — no change needed";
            StatusMessage = $"{preset.Name}: unblocked {done.Count}, already reachable {clear.Count}" +
                            (failed.Count > 0 ? $", failed: {string.Join("; ", failed)}" : ".");
        }, "Preset failed");
        Reload();
    }

    /// <summary>
    /// "Scan &amp; fix": finds blocked Steam servers beyond the presets (Steam logs, store pages, DNS cache),
    /// asks <paramref name="confirm"/> which to fix (null = cancel), then unblocks the chosen ones.
    /// Only Steam domains are ever considered — see <see cref="SteamDomains"/>.
    /// </summary>
    public Task ScanSteamAsync(Func<IReadOnlyList<ScanFinding>, IReadOnlyList<ScanFinding>?> confirm) => RunGuardedAsync(async () =>
    {
        RequireElevation();
        var scanner = _scanner ?? throw new UnblockException("scanning isn't available.");
        var result = await scanner.ScanAsync(new Progress<string>(note => ScanNote = note));
        LogScan(result);

        if (result.Blocked.Count == 0)
        {
            ScanNote = $"Checked {result.Checked} Steam servers — nothing else is blocked.";
            StatusMessage = "Steam scan: " + ScanNote;
            return;
        }

        var chosen = confirm(result.Blocked);
        if (chosen is null || chosen.Count == 0)
        {
            ScanNote = $"Found {result.Blocked.Count} blocked Steam server(s); nothing changed.";
            return;
        }
        await ApplyFindingsAsync(chosen);
    }, "Steam scan failed");

    private async Task ApplyFindingsAsync(IReadOnlyList<ScanFinding> chosen)
    {
        var failed = new List<string>();
        try
        {
            foreach (var (finding, i) in chosen.Select((f, i) => (f, i)))
            {
                ScanNote = $"Unblocking {i + 1} of {chosen.Count}: {finding.Domain}…";
                try { await _manager.UnblockAsync(finding.Diagnosis); }
                catch (UnblockException ex) { failed.Add($"{finding.Domain} ({ex.Message})"); }
            }
        }
        finally
        {
            Reload();
        }
        ScanNote = $"Unblocked {chosen.Count - failed.Count} Steam server(s)" +
                   (failed.Count > 0 ? $", {failed.Count} failed." : ".");
        StatusMessage = "Steam scan: " + ScanNote + (failed.Count > 0 ? $" Failed: {string.Join("; ", failed)}" : "");
    }

    // Findings go to the log with their sources, so new hosts can be added to the presets later.
    private void LogScan(ScanResult result)
    {
        var details = string.Join("\n", result.Blocked.Select(f =>
            $"{f.Domain}  {KindLabel(f.Diagnosis.Kind)}  via {string.Join(", ", f.Sources)}"));
        if (result.Problems.Count > 0)
            details += (details.Length > 0 ? "\n" : "") + "problems:\n" + string.Join("\n", result.Problems);
        _log?.Info("Unblock", $"Steam scan checked {result.Checked} servers, {result.Blocked.Count} blocked.", details);
    }

    private async Task RemovePresetAsync(UnblockPresetViewModel row)
    {
        await RunGuardedAsync(async () =>
        {
            RequireElevation();
            row.Note = "";
            var active = ActiveDomainsOf(row.Preset);
            foreach (var domain in active)
                await _manager.RemoveAsync(domain);
            StatusMessage = $"{row.Preset.Name}: {active.Count} domain(s) back to normal.";
        }, "Remove failed");
        Reload();
    }

    private List<string> ActiveDomainsOf(UnblockPreset preset) =>
        preset.Domains.Where(d => _manager.Domains.Any(a => a.Domain == d)).ToList();

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
            vm.RelayDown = !status.TryGetValue(vm.Domain, out var s);
            vm.Health = s is not null
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
        foreach (var row in Presets)
            row.SetActiveCount(ActiveDomainsOf(row.Preset).Count);
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(Summary));
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
