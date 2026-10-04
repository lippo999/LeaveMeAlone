using LeaveMeAlone.Models;

namespace LeaveMeAlone.Services;

/// <summary>
/// One row of the "Quick" block screen: a detected launcher (with or without a bundle yet) or a
/// user bundle that isn't tied to a launcher.
/// </summary>
public sealed record QuickBlockItem(string Title, DetectedLauncher? Launcher, Bundle? Bundle)
{
    public bool IsOn => Bundle?.Enabled == true;
}

/// <summary>
/// Maps detected launchers onto bundles. A launcher owns the first bundle carrying its exact name
/// (case-insensitive) — that's the bundle its switch creates — so renaming it in Custom detaches it.
/// </summary>
public static class QuickBlock
{
    public static IReadOnlyList<QuickBlockItem> Build(IReadOnlyList<DetectedLauncher> launchers, IReadOnlyList<Bundle> bundles)
    {
        var linked = new HashSet<string>();
        var items = new List<QuickBlockItem>();
        foreach (var launcher in launchers)
        {
            var bundle = bundles.FirstOrDefault(b =>
                !linked.Contains(b.Id) && string.Equals(b.Name, launcher.Name, StringComparison.OrdinalIgnoreCase));
            if (bundle != null) linked.Add(bundle.Id);
            items.Add(new QuickBlockItem(launcher.Name, launcher, bundle));
        }
        items.AddRange(bundles.Where(b => !linked.Contains(b.Id)).Select(b => new QuickBlockItem(b.Name, null, b)));
        return items;
    }

    public static IReadOnlyList<FirewallTarget> TargetsFor(DetectedLauncher launcher) =>
        launcher.ExistingPaths.Select(path => CommonLaunchers.ToTarget(launcher.Name, path)).ToList();
}
