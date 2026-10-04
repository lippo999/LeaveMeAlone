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

/// <summary>Registers/unregisters the app to launch at Windows sign-in (per-user Run key).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LeaveMeAlone";
    private const string LegacyValueName = "FirewallBundleBlocker";

    /// <summary>Replaces a Run entry left by the app's former name with one for this executable.</summary>
    public static void MigrateLegacy()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(LegacyValueName) is null) return;

        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        SetEnabled(true);
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(ValueName, $"\"{exe}\"");
        }
        else if (key.GetValue(ValueName) != null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
