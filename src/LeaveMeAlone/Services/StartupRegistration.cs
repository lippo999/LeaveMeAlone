using Microsoft.Win32;

namespace LeaveMeAlone.Services;

public sealed class StartupException : Exception
{
    public StartupException(string message) : base(message) { }
}

/// <summary>The per-user <c>HKCU\...\Run</c> key, where older versions registered autostart.</summary>
public interface IRunKey
{
    bool Contains(string valueName);
    void Remove(string valueName);
}

public sealed class RegistryRunKey : IRunKey
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool Contains(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(valueName) != null;
    }

    public void Remove(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

/// <summary>
/// Launches the app at sign-in through a Task Scheduler task that runs with highest privileges.
/// A Run-key entry cannot do this: the app requires administrator, and Windows silently skips
/// Run entries that need elevation, so the app never started after a reboot.
/// </summary>
public sealed class StartupRegistration
{
    public const string TaskPath = @"\LeaveMeAlone\";
    private static readonly string[] RunKeyValueNames = ["LeaveMeAlone", "FirewallBundleBlocker"];

    private readonly IProcessRunner _runner;
    private readonly IRunKey _runKey;
    private readonly string _exePath;
    private readonly string _userId;

    /// <param name="userId">Account the task signs in as, e.g. <c>DOMAIN\user</c>.</param>
    public StartupRegistration(IProcessRunner runner, IRunKey runKey, string exePath, string userId)
    {
        _runner = runner;
        _runKey = runKey;
        _exePath = exePath;
        _userId = userId;
    }

    /// <summary>One task per account, so each admin user on the PC keeps their own setting.</summary>
    public string TaskName => "Start at sign-in (" + _userId.Replace('\\', '_') + ")";

    /// <summary>
    /// Moves an old Run-key entry onto a scheduled task and repoints an existing task if the exe moved.
    /// Returns whether autostart is enabled afterwards.
    /// </summary>
    public async Task<bool> InitializeAsync(CancellationToken ct = default)
    {
        var legacy = RunKeyValueNames.Where(_runKey.Contains).ToList();
        var registeredExe = await GetRegisteredExeAsync(ct).ConfigureAwait(false);

        var enabled = legacy.Count > 0 || registeredExe != null;
        var stale = registeredExe != null && !string.Equals(registeredExe, _exePath, StringComparison.OrdinalIgnoreCase);
        if (legacy.Count > 0 || stale)
            await RegisterAsync(ct).ConfigureAwait(false);

        // Only drop the old entries once the task exists, so a failure is retried next launch.
        foreach (var name in legacy) _runKey.Remove(name);
        return enabled;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
        => await GetRegisteredExeAsync(ct).ConfigureAwait(false) != null;

    public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
        => enabled ? RegisterAsync(ct) : UnregisterAsync(ct);

    // The exe the task launches, or null when there is no task.
    private async Task<string?> GetRegisteredExeAsync(CancellationToken ct)
    {
        var script = $"$t = Get-ScheduledTask {TaskArgs()} -ErrorAction SilentlyContinue; " +
                     "if ($t) { [Console]::Out.Write($t.Actions[0].Execute) }";
        var result = await RunAsync(script, ct).ConfigureAwait(false);
        var exe = result.StdOut.Trim();
        return exe.Length == 0 ? null : exe;
    }

    private Task RegisterAsync(CancellationToken ct)
    {
        var user = PowerShellScript.Quote(_userId);
        var script =
            $"$a = New-ScheduledTaskAction -Execute {PowerShellScript.Quote(_exePath)}; " +
            $"$tr = New-ScheduledTaskTrigger -AtLogOn -User {user}; " +
            $"$p = New-ScheduledTaskPrincipal -UserId {user} -LogonType Interactive -RunLevel Highest; " +
            // Defaults would skip the task on battery and kill the app after 72 hours.
            "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
            "-ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew; " +
            $"Register-ScheduledTask {TaskArgs()} -Action $a -Trigger $tr -Principal $p -Settings $s -Force | Out-Null";
        return RunAsync(script, ct);
    }

    private Task UnregisterAsync(CancellationToken ct)
        => RunAsync($"Unregister-ScheduledTask {TaskArgs()} -Confirm:$false -ErrorAction SilentlyContinue", ct);

    private string TaskArgs()
        => $"-TaskName {PowerShellScript.Quote(TaskName)} -TaskPath {PowerShellScript.Quote(TaskPath)}";

    private async Task<ProcessResult> RunAsync(string script, CancellationToken ct)
    {
        var result = await PowerShellScript.RunAsync(_runner, script, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new StartupException(PowerShellScript.ErrorText(result));
        return result;
    }
}
