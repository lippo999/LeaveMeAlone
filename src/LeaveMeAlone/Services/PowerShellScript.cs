namespace LeaveMeAlone.Services;

/// <summary>Builds and runs one-shot <c>powershell.exe -Command</c> scripts through an <see cref="IProcessRunner"/>.</summary>
public static class PowerShellScript
{
    /// <summary>
    /// Runs <paramref name="script"/> so that any failing cmdlet yields a non-zero exit code with the message on stderr.
    /// Returns the raw result; callers decide which exception to throw.
    /// </summary>
    public static Task<ProcessResult> RunAsync(IProcessRunner runner, string script, CancellationToken ct = default)
    {
        var wrapped = "$ErrorActionPreference='Stop'; try { " + script +
                      "; exit 0 } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{wrapped.Replace("\"", "\\\"")}\"";
        return runner.RunAsync("powershell.exe", args, ct);
    }

    /// <summary>The readable error text of a failed run (stderr, else stdout).</summary>
    public static string ErrorText(ProcessResult result)
        => (string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr).Trim();

    /// <summary>Escapes a value for a PowerShell single-quoted string (a quote is doubled).</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
