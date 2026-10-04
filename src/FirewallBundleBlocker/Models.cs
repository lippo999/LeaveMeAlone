namespace FirewallBundleBlocker.Models;

/// <summary>What a firewall target matches on.</summary>
public enum TargetType
{
    /// <summary>Block an executable's outbound traffic (matched by image path).</summary>
    Program,

    /// <summary>Block a remote IP address or CIDR range (optionally a port/protocol).</summary>
    RemoteAddress,
}

/// <summary>
/// A single thing a bundle blocks. Immutable — edits produce a new instance via <c>with</c>.
/// </summary>
public sealed record FirewallTarget
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public TargetType Type { get; init; }

    /// <summary>Program path (Program) or IP/CIDR (RemoteAddress).</summary>
    public string Value { get; init; } = "";

    /// <summary>Remote port(s) for RemoteAddress targets. Null/empty = all ports.</summary>
    public string? RemotePort { get; init; }

    /// <summary>"Any", "TCP", or "UDP". Only meaningful for RemoteAddress targets.</summary>
    public string Protocol { get; init; } = "Any";

    /// <summary>Human-friendly label shown in the UI.</summary>
    public string DisplayName { get; init; } = "";
}

/// <summary>
/// A named, user-created group of firewall targets that can be toggled on/off as one unit.
/// Immutable — mutations return a new instance.
/// </summary>
public sealed record Bundle
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public bool Enabled { get; init; }
    public IReadOnlyList<FirewallTarget> Targets { get; init; } = Array.Empty<FirewallTarget>();
}
