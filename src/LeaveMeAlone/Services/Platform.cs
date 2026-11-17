using System.Diagnostics;
using System.Security.Principal;

namespace LeaveMeAlone.Services;

/// <summary>Windows-specific helpers for elevation.</summary>
public static class Platform
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Relaunches this executable with a UAC elevation prompt. Returns false if it could not start.</summary>
    public static bool TryRelaunchAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch
        {
            // User declined the UAC prompt, or elevation is unavailable.
            return false;
        }
    }
}
