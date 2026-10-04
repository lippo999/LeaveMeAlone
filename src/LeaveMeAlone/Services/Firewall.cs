using System.Diagnostics;
using System.Text;
using LeaveMeAlone.Models;

namespace LeaveMeAlone.Services;

/// <summary>
/// Naming scheme for the firewall rules this app owns. Every rule is tagged with the
/// <see cref="Prefix"/> and grouped per bundle, so the app only ever touches its own rules.
/// </summary>
public static class FirewallRuleNaming
{
    public const string Prefix = "FBB";
    public static string GroupFor(string bundleId) => $"{Prefix}::{bundleId}";
    public static string DisplayNameFor(string bundleId, string targetId) => $"{Prefix}::{bundleId}::{targetId}";
}

public sealed class FirewallException : Exception
{
    public FirewallException(string message) : base(message) { }
}

public interface IFirewallService
{
    /// <summary>Recreate every rule for the bundle to match config, honoring <see cref="Bundle.Enabled"/>.</summary>
    Task ApplyBundleAsync(Bundle bundle, CancellationToken ct = default);

    /// <summary>Create (or refresh) the single rule for one target.</summary>
    Task ApplyTargetAsync(Bundle bundle, FirewallTarget target, CancellationToken ct = default);

    Task RemoveTargetAsync(string bundleId, string targetId, CancellationToken ct = default);
    Task SetBundleEnabledAsync(string bundleId, bool enabled, CancellationToken ct = default);
    Task RemoveBundleAsync(string bundleId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetLiveRuleDisplayNamesAsync(string bundleId, CancellationToken ct = default);
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxLoggedChars = 2000;
    private readonly AppLog? _log;

    /// <param name="log">When set, every command is logged (exit code + arguments; stderr on failure).</param>
    public ProcessRunner(AppLog? log = null) => _log = log;

    public async Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct = default)
    {
        var started = DateTimeOffset.Now;
        var result = await RunCoreAsync(fileName, arguments, ct).ConfigureAwait(false);
        Log(fileName, arguments, result, DateTimeOffset.Now - started);
        return result;
    }

    private void Log(string fileName, string arguments, ProcessResult result, TimeSpan took)
    {
        if (_log is null) return;
        var ok = result.ExitCode == 0;
        var details = $"args: {Truncate(arguments)}" +
                      (ok || string.IsNullOrWhiteSpace(result.StdErr) ? "" : $"\nstderr: {Truncate(result.StdErr.Trim())}");
        _log.Write(ok ? LogLevel.Debug : LogLevel.Warn, "System",
            $"{fileName} exited {result.ExitCode} ({took.TotalMilliseconds:0} ms)", details);
    }

    private static string Truncate(string text) =>
        text.Length <= MaxLoggedChars ? text : text[..MaxLoggedChars] + "…";

    private static async Task<ProcessResult> RunCoreAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdOutTask = proc.StandardOutput.ReadToEndAsync();
        var stdErrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return new ProcessResult(proc.ExitCode,
            await stdOutTask.ConfigureAwait(false),
            await stdErrTask.ConfigureAwait(false));
    }
}

/// <summary>
/// Drives Windows Firewall through the built-in NetSecurity PowerShell cmdlets.
/// Rules are grouped per bundle so an entire bundle toggles in one call.
/// </summary>
public sealed class PowerShellFirewallService : IFirewallService
{
    private readonly IProcessRunner _runner;

    public PowerShellFirewallService(IProcessRunner runner) => _runner = runner;

    public Task ApplyBundleAsync(Bundle bundle, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append(RemoveGroupScript(bundle.Id));
        foreach (var target in bundle.Targets)
            sb.Append("; ").Append(CreateRuleScript(bundle, target));
        return RunScriptAsync(sb.ToString(), ct);
    }

    public Task ApplyTargetAsync(Bundle bundle, FirewallTarget target, CancellationToken ct = default)
        => RunScriptAsync(CreateRuleScript(bundle, target), ct);

    public Task RemoveTargetAsync(string bundleId, string targetId, CancellationToken ct = default)
    {
        var name = FirewallRuleNaming.DisplayNameFor(bundleId, targetId);
        return RunScriptAsync($"Remove-NetFirewallRule -DisplayName '{Esc(name)}' -ErrorAction SilentlyContinue", ct);
    }

    public Task SetBundleEnabledAsync(string bundleId, bool enabled, CancellationToken ct = default)
    {
        var group = FirewallRuleNaming.GroupFor(bundleId);
        var cmd = enabled ? "Enable-NetFirewallRule" : "Disable-NetFirewallRule";
        return RunScriptAsync($"{cmd} -Group '{Esc(group)}' -ErrorAction SilentlyContinue", ct);
    }

    public Task RemoveBundleAsync(string bundleId, CancellationToken ct = default)
        => RunScriptAsync(RemoveGroupScript(bundleId), ct);

    public async Task<IReadOnlyList<string>> GetLiveRuleDisplayNamesAsync(string bundleId, CancellationToken ct = default)
    {
        var group = FirewallRuleNaming.GroupFor(bundleId);
        var script = $"Get-NetFirewallRule -Group '{Esc(group)}' -ErrorAction SilentlyContinue " +
                     "| Select-Object -ExpandProperty DisplayName";
        var result = await RunScriptAsync(script, ct).ConfigureAwait(false);
        return result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static string RemoveGroupScript(string bundleId)
        => $"Remove-NetFirewallRule -Group '{Esc(FirewallRuleNaming.GroupFor(bundleId))}' -ErrorAction SilentlyContinue";

    private static string CreateRuleScript(Bundle bundle, FirewallTarget target)
    {
        var name = FirewallRuleNaming.DisplayNameFor(bundle.Id, target.Id);
        var group = FirewallRuleNaming.GroupFor(bundle.Id);
        var enabled = bundle.Enabled ? "True" : "False";

        var sb = new StringBuilder();
        // Idempotent: drop any stale rule with the same name first.
        sb.Append($"Remove-NetFirewallRule -DisplayName '{Esc(name)}' -ErrorAction SilentlyContinue; ");
        sb.Append($"New-NetFirewallRule -DisplayName '{Esc(name)}' -Group '{Esc(group)}' " +
                  $"-Direction Outbound -Action Block -Enabled {enabled}");

        if (target.Type == TargetType.Program)
        {
            sb.Append($" -Program '{Esc(target.Value)}'");
        }
        else
        {
            sb.Append($" -RemoteAddress '{Esc(target.Value)}'");
            var protocol = NormalizeProtocol(target.Protocol, target.RemotePort);
            if (!string.IsNullOrWhiteSpace(target.RemotePort))
                sb.Append($" -Protocol {protocol} -RemotePort '{Esc(target.RemotePort!)}'");
            else if (!string.Equals(protocol, "Any", StringComparison.OrdinalIgnoreCase))
                sb.Append($" -Protocol {protocol}");
        }

        return sb.ToString();
    }

    // A concrete port requires a concrete protocol; default to TCP when a port is set but protocol is Any.
    private static string NormalizeProtocol(string? protocol, string? port)
    {
        var p = (protocol ?? "Any").Trim();
        if (string.Equals(p, "TCP", StringComparison.OrdinalIgnoreCase)) return "TCP";
        if (string.Equals(p, "UDP", StringComparison.OrdinalIgnoreCase)) return "UDP";
        return string.IsNullOrWhiteSpace(port) ? "Any" : "TCP";
    }

    private async Task<ProcessResult> RunScriptAsync(string script, CancellationToken ct)
    {
        // Wrap so a failing cmdlet produces a non-zero exit code with a readable message.
        var wrapped = "$ErrorActionPreference='Stop'; try { " + script +
                      "; exit 0 } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{wrapped.Replace("\"", "\\\"")}\"";

        var result = await _runner.RunAsync("powershell.exe", args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            throw new FirewallException(message.Trim());
        }
        return result;
    }

    // PowerShell single-quoted strings escape a quote by doubling it.
    private static string Esc(string value) => value.Replace("'", "''");
}
