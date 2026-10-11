namespace GKME.Core;

/// <summary>A selectable LAN interface (name + IPv4 address) used to build the
/// web controller URL shown to the user.</summary>
public sealed class LanInterfaceInfo
{
    public string Name { get; init; } = "";
    public string Address { get; init; } = "";

    public string Display => string.IsNullOrEmpty(Name) ? Address : $"{Name} ({Address})";

    public override string ToString() => Display;
}
