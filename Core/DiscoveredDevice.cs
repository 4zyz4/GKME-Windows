using System.Net;

namespace GKME.Core;

public class DiscoveredDevice
{
    public string DeviceName { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public required IPAddress IpAddress { get; init; }
    public string IpString => IpAddress.ToString();
    public int Port { get; init; } = 37284;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string Key => $"{IpAddress}:{Port}";

    // Identity key: the MAC when known (stable across IP changes), otherwise the
    // IP. Used to group multiple IPs that belong to the same physical phone.
    public string GroupKey => string.IsNullOrWhiteSpace(MacAddress) ? IpString : MacAddress;

    // Persisted-mode key. The emulated controller type is stored per physical
    // device, so it follows the MAC rather than the (volatile) IP.
    public string ModeKey => GroupKey;
}
