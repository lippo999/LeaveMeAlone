using System.IO;
using LeaveMeAlone.Models;

namespace LeaveMeAlone.Services;

public sealed record LauncherPreset(string Name, IReadOnlyList<string> CandidatePaths);

public sealed record DetectedLauncher(string Name, IReadOnlyList<string> ExistingPaths);

/// <summary>
/// Convenience presets for common game launchers. These are just quick-add helpers —
/// nothing here is hardcoded into a bundle; the user still chooses what to block.
/// Candidate paths contain environment variables expanded at detection time.
/// </summary>
public static class CommonLaunchers
{
    public static readonly IReadOnlyList<LauncherPreset> Presets = new List<LauncherPreset>
    {
        new("Steam", new[]
        {
            @"%ProgramFiles(x86)%\Steam\steam.exe",
            @"%ProgramFiles(x86)%\Steam\bin\cef\cef.win7x64\steamwebhelper.exe",
        }),
        new("Ubisoft Connect", new[]
        {
            @"%ProgramFiles(x86)%\Ubisoft\Ubisoft Game Launcher\UbisoftConnect.exe",
            @"%ProgramFiles(x86)%\Ubisoft\Ubisoft Game Launcher\upc.exe",
        }),
        new("Epic Games Launcher", new[]
        {
            @"%ProgramFiles(x86)%\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
        }),
        new("EA app", new[]
        {
            @"%ProgramFiles%\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe",
        }),
        new("Battle.net", new[]
        {
            @"%ProgramFiles(x86)%\Battle.net\Battle.net.exe",
        }),
        new("GOG Galaxy", new[]
        {
            @"%ProgramFiles(x86)%\GOG Galaxy\GalaxyClient.exe",
        }),
        new("Riot Client", new[]
        {
            @"%ProgramFiles%\Riot Games\Riot Client\RiotClientServices.exe",
        }),
        new("Rockstar Games Launcher", new[]
        {
            @"%ProgramFiles%\Rockstar Games\Launcher\Launcher.exe",
        }),
    };

    /// <summary>Presets whose executables actually exist on this machine.</summary>
    public static IReadOnlyList<DetectedLauncher> Detect()
    {
        var found = new List<DetectedLauncher>();
        foreach (var preset in Presets)
        {
            var existing = preset.CandidatePaths
                .Select(Environment.ExpandEnvironmentVariables)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (existing.Count > 0)
                found.Add(new DetectedLauncher(preset.Name, existing));
        }
        return found;
    }

    public static FirewallTarget ToTarget(string launcherName, string path) => new()
    {
        Type = TargetType.Program,
        Value = path,
        DisplayName = $"{launcherName} — {Path.GetFileName(path)}",
    };
}
